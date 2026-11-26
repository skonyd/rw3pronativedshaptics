using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
#if HAS_HIDMAESTRO
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>
/// GIP proxy (docs/research/GIP_VIRTUAL_PAD.md, "Next: a transparent GIP proxy"; experimental, opt-in by flag file).
/// Windows' Xbox driver needs the pad's own security answer (GIP 0x06, E-20261123-75), so a stand-alone virtual pad cannot
/// work. Here the virtual device is only a window: a clone of the Wolverine (1532:0A3F, XGIP10, see
/// <see cref="GipVirtualPadProbe"/>), and every GIP message goes through RW3 unchanged:
/// <list type="bullet">
/// <item>Windows → clone → RW3 → real pad (direct-mode pipe): announce answers, identify, authentication, power, LED,
/// rumble 0x09 with the trigger motors, ACKs.</item>
/// <item>real pad → RW3 → clone → Windows: announce, identify data, authentication answers, input, Xbox button, ACKs.
/// Razer's replies to RW3's own Sensa frames (0x10) stay with RW3.</item>
/// </list>
/// So the genuine pad authenticates itself, games see a wired Xbox controller and send it everything, impulse triggers
/// included. RW3 reads the game's rumble on the way and also plays the body part in the palms (the palms carry it while
/// RW3 streams Sensa frames). Needs: the pad on WinUSB (direct mode) and nothing else creating a virtual Xbox pad, i.e.
/// the output "Kolun kendisi".
/// </summary>
public static class GipProxy
{
    public const string ProfileId = "rw3-gip-proxy";
    public static string FlagFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "gip-proxy.flag");
    public static volatile bool Running;

    /// <summary>The clone profile of the probe, under its own id and serial.</summary>
    public static string BuildProfileJson() => GipVirtualPadProbe.BuildProfileJson()
        .Replace("\"id\": \"" + GipVirtualPadProbe.ProfileId + "\"", "\"id\": \"" + ProfileId + "\"")
        .Replace("RW3 GIP probe (Wolverine clone)", "RW3 GIP proxy (Wolverine)")
        .Replace("RW3GIPPROBE0001", "RW3GIPPROXY0001");

    /// <summary>Starts the proxy for this RW3 run when its flag file exists (it stays; delete it to turn the proxy off).</summary>
    public static void RunIfRequested()
    {
        try { if (!Environment.IsPrivilegedProcess || !File.Exists(FlagFile)) return; }
        catch { return; }
        WinUsbGipTransport.ProxyPending = true;   // from the start: RW3 never powers, acks or asks the pad in this run
        new Thread(Run) { IsBackground = true, Name = "RW3 GIP proxy" }.Start();
    }

    private static void Log(string m) => DiagnosticsService.Info("GIP proxy: " + m);
    private static int s_traced;
    /// <summary>The first 60 messages both ways, raw (the first run showed 0x7A / 0x00 messages going to Windows).</summary>
    private static void Trace(string direction, byte[] msg)
    {
        if (Interlocked.Increment(ref s_traced) <= 60) Log($"{direction} {msg.Length,2}: {Convert.ToHexString(msg.AsSpan(0, Math.Min(40, msg.Length)))}");
    }

    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static void Count(string direction, byte command) => Counts.AddOrUpdate($"{direction}{command:X2}", 1, (_, c) => c + 1);
    private static string TakeCounts()
    {
        var s = string.Join(" ", Counts.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"));
        Counts.Clear();
        return s.Length == 0 ? "-" : s;
    }

#if HAS_HIDMAESTRO
    // ── Guards (E-20261123-77: a run left Windows' device manager stuck until a restart) ──
    // One clone per RW3 run, never re-created; the pad is reset before the clone exists, so it does not leave USB under
    // it; the run ends when the pad leaves anyway, when the time limit is up, or when Windows' device manager stops
    // answering (then the flag file is deleted, so the next RW3 start does not run the proxy again).

    /// <summary>Default run time; the flag file may hold another number of seconds (10..3600).</summary>
    public const int DefaultMaxSeconds = 300;
    private static volatile bool s_abort;
    private static volatile string? s_abortReason;

    private static void Abort(string reason, bool deleteFlag)
    {
        if (s_abort) return;
        s_abortReason = reason; s_abort = true;
        Log("STOPPING: " + reason);
        WinUsbGipTransport.ProxySink = null;   // nothing more goes either way
        if (deleteFlag) { try { File.Delete(FlagFile); Log("flag file deleted: the next RW3 start runs without the proxy."); } catch { } }
    }

    /// <summary>A Configuration Manager query (the one direct mode uses to find the pad) that must return within
    /// <paramref name="timeoutMs"/>. When the device manager is stuck it blocks; the stuck worker is left behind.</summary>
    private static bool PnpAnswers(int timeoutMs)
    {
        var t = System.Threading.Tasks.Task.Run(WinUsbGipTransport.DevicePresent);
        return t.Wait(timeoutMs);
    }

    private static void StartPnpWatchdog()
    {
        new Thread(() =>
        {
            while (Running && !s_abort)
            {
                Thread.Sleep(3000);
                if (!Running || s_abort) break;
                if (!PnpAnswers(5000))
                    Abort("Windows' device manager did not answer within 5 s (a stuck device operation). Restart Windows; the proxy is off until the flag file is set again.", deleteFlag: true);
            }
        }) { IsBackground = true, Name = "RW3 GIP proxy PnP watchdog" }.Start();
    }

    /// <summary>Runs <paramref name="work"/> with a time limit; false (and the work left running) when it takes longer.</summary>
    private static bool Within(int timeoutMs, Action work, string what)
    {
        var t = System.Threading.Tasks.Task.Run(work);
        try { if (t.Wait(timeoutMs)) return true; }
        catch (AggregateException ex) { throw ex.InnerException ?? ex; }
        Log($"{what} took over {timeoutMs / 1000} s: given up.");
        return false;
    }

    private static int MaxSeconds()
    {
        try { if (int.TryParse(File.ReadAllText(FlagFile).Trim(), out int s)) return Math.Clamp(s, 10, 3600); } catch { }
        return DefaultMaxSeconds;
    }

#endif

    private static void Run()
    {
#if HAS_HIDMAESTRO
        Thread.Sleep(3000);
        var transport = WinUsbGipTransport.Shared;
        int maxSeconds = MaxSeconds();
        if (!PnpAnswers(5000)) { Abort("Windows' device manager is not answering before the proxy even started; restart Windows.", deleteFlag: true); return; }
        // wait for the pad as long as RW3 runs: the adapter is on USB only while the pad is on and linked
        bool waitingLogged = false;
        while (!transport.IsOpen)
        {
            transport.TryOpen();
            if (transport.IsOpen) break;
            if (!waitingLogged) { waitingLogged = true; Log("waiting for the pad on WinUSB (switch it on; adapter or cable plugged in)."); }
            Thread.Sleep(2000);
        }
        // Reset the pad first, while no clone exists: it leaves USB (the adapter re-enumerates) and comes back with an
        // announce, which the transport keeps. The clone then gets that announce, so the pad need not leave again.
        // Until 2026-11-26 the reset came after the clone, and the pad left and returned under it (E-20261123-77).
        WinUsbGipTransport.ProxyHeldAnnounce = null;
        Log($"resetting the pad before the clone exists; run limit {maxSeconds} s.");
        transport.RequestPowerReset();
        long announceBy = Environment.TickCount64 + 20_000;
        while (WinUsbGipTransport.ProxyHeldAnnounce == null && Environment.TickCount64 < announceBy)
        {
            if (!transport.IsOpen && !WinUsbGipTransport.Suspended) transport.TryOpen();
            Thread.Sleep(100);
        }
        var announce = WinUsbGipTransport.ProxyHeldAnnounce;
        if (announce == null) { Log("the pad did not announce within 20 s after its reset; proxy not started (flag kept)."); return; }
        if (!PnpAnswers(5000)) { Abort("Windows' device manager stopped answering after the pad's reset; clone not created. Restart Windows.", deleteFlag: true); return; }
        string dir = Path.Combine(Path.GetTempPath(), "rw3-gip-proxy");
        HMController? ctrl = null;
        HMContext? ctx = null;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProfileId + ".json"), BuildProfileJson(), new UTF8Encoding(false));
            ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.LoadProfilesFromDirectory(dir);
            var issues = string.Join(" | ", ctx.ProfileLoadIssues.Where(i => i.ProfileId == ProfileId || (i.File ?? "").Contains("rw3-gip-proxy")).Select(i => $"{i.JsonPath}: {i.Reason}"));
            var profile = ctx.GetProfile(ProfileId);
            if (profile == null) { Log("profile refused: " + issues); return; }
            var c0 = ctx;
            if (!Within(45_000, () => ctrl = c0.CreateController(profile, "rw3-gip-proxy"), "creating the clone"))
            {
                Abort("creating the clone did not finish; restart Windows if devices stop appearing.", deleteFlag: true);
                return;
            }
            var gate = new object();
            var c = ctrl!;
            void ToWindows(byte[] msg)
            {
                if (msg.Length == 0) return;
                Count("pad→win ", msg[0]);
                Trace("pad→win", msg);
                // exact length, as the pad sends it (a short packet). Run 5 (20:51) padded every message to 64 bytes with
                // zeros; Windows then repeated its ACKs by the thousand during authentication. Padded only if HIDMaestro
                // refuses a short report.
                lock (gate)
                {
                    try { c.SubmitRawReport(msg); if (Interlocked.Exchange(ref s_exactNoted, 1) == 0) Log("messages go to Windows at their exact length."); }
                    catch (Exception ex)
                    {
                        if (Interlocked.Exchange(ref s_paddedNoted, 1) == 0) Log("exact-length report refused (" + ex.Message + "); padding to 64 bytes.");
                        var report = new byte[64];
                        msg.AsSpan(0, Math.Min(64, msg.Length)).CopyTo(report);
                        try { c.SubmitRawReport(report); } catch { }
                    }
                }
            }
            var recent = new System.Collections.Generic.List<(byte[] Msg, long At)>();
            double rumL = 0, rumR = 0;
            long rumTick = 0;
            c.OutputReceived += (_, pkt) =>
            {
                var msg = new byte[1 + pkt.Data.Length];
                msg[0] = pkt.ReportId;
                pkt.Data.Span.CopyTo(msg.AsSpan(1));
                // The same output came again and again 4 ms apart (an ACK 4×, a chunk end 12×: run 7, 20:58) until the next
                // one; a real Windows-to-pad exchange sends each once, and the copies broke the pad's authentication.
                // Forward a message once; Windows' own retries carry a new GIP sequence number or come later.
                long now = Environment.TickCount64;
                // Not only the last one: an older output came back after a newer one too (run 8, 21:01: a chunk end #58
                // again as #67 after an ACK #66), and the pad dropped its reply midway. Compare with the last 16 within 1 s.
                lock (gate)
                {
                    recent.RemoveAll(r => now - r.At > 1000);
                    if (recent.Exists(r => r.Msg.AsSpan().SequenceEqual(msg))) { Interlocked.Increment(ref s_dupes); return; }
                    recent.Add((msg, now));
                    if (recent.Count > 16) recent.RemoveAt(0);
                }
                Count("win→pad ", msg[0]);
                Trace($"win→pad #{pkt.SeqNo}", msg);
                // the game's rumble on the way: [09][opt][seq][len][00 0F LT RT L R …], 0..100
                if (msg[0] == GipCodec.CmdRumble && msg.Length >= 10)
                {
                    rumL = msg[8] / 100.0; rumR = msg[9] / 100.0;
                    Interlocked.Exchange(ref rumTick, Environment.TickCount64);
                    lastRumble = Convert.ToHexString(msg);
                }
                transport.SendRawMessage(msg);
                // Windows powered the pad on ([05][opt][seq][01][00]): the pad's light, which a restart cleared
                if (msg[0] == GipCodec.CmdPower && msg.Length >= 5 && msg[3] == 1 && msg[4] == 0x00) transport.RefreshConnectedLight();
            };
            WinUsbGipTransport.ProxySink = ToWindows;
            Running = true;
            StartPnpWatchdog();
            Log("clone created; giving Windows the pad's announce.");
            ToWindows(announce);
            long endAt = Environment.TickCount64 + maxSeconds * 1000L;
            while (!s_abort)
            {
                // the palms play the body rumble while it lasts (Windows sends rumble on change only)
                if (rumL > 0.01 || rumR > 0.01) XboxSensa.SetRumble(rumL, rumR);
                if (Environment.TickCount64 - Interlocked.Read(ref nextLog) >= 0)
                {
                    Interlocked.Exchange(ref nextLog, Environment.TickCount64 + 10_000);
                    Log($"10 s: {TakeCounts()}; duplicate outputs dropped {Interlocked.Exchange(ref s_dupes, 0)}; last rumble {lastRumble}; pad {transport.Summary}");
                }
                // The pad left (Windows switched it off after a failed authentication, or the adapter went): end the run
                // instead of waiting for it under the clone. Run 8 (2026-11-23 21:01) waited and reopened, and the
                // device manager stuck.
                if (!transport.IsOpen) { Abort("the pad left USB while the clone was up; run ended (flag kept, restart RW3 to try again).", deleteFlag: false); break; }
                if (Environment.TickCount64 >= endAt) { Abort($"run limit of {maxSeconds} s reached (flag kept).", deleteFlag: false); break; }
                Thread.Sleep(50);
            }
        }
        catch (Exception ex) { Log("stopped: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            WinUsbGipTransport.ProxySink = null;
            Running = false;
            var dispose = ctrl;
            var dctx = ctx;
            if (dispose != null && !Within(15_000, () => { try { dispose.Dispose(); } catch { } }, "removing the clone"))
                Abort("removing the clone did not finish; restart Windows if devices stop appearing.", deleteFlag: true);
            else try { dctx?.Dispose(); } catch { }
            Log("ended" + (s_abortReason != null ? ": " + s_abortReason : "") + ".");
        }
#else
        Log("built without HIDMaestro.");
#endif
    }

#if HAS_HIDMAESTRO
    private static long nextLog;
    private static int s_exactNoted, s_paddedNoted;
    private static long s_dupes;
    private static string lastRumble = "-";
#endif
}
