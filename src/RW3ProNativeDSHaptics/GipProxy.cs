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

    private static void Run()
    {
#if HAS_HIDMAESTRO
        Thread.Sleep(3000);
        var transport = WinUsbGipTransport.Shared;
        for (int i = 0; i < 30 && !transport.IsOpen; i++) { transport.TryOpen(); Thread.Sleep(1000); }
        if (!transport.IsOpen) { Log("the pad is not open over WinUSB (direct mode, adapter or cable plugged in?); not started."); return; }
        string dir = Path.Combine(Path.GetTempPath(), "rw3-gip-proxy");
        HMController? ctrl = null;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProfileId + ".json"), BuildProfileJson(), new UTF8Encoding(false));
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.LoadProfilesFromDirectory(dir);
            var issues = string.Join(" | ", ctx.ProfileLoadIssues.Where(i => i.ProfileId == ProfileId || (i.File ?? "").Contains("rw3-gip-proxy")).Select(i => $"{i.JsonPath}: {i.Reason}"));
            var profile = ctx.GetProfile(ProfileId);
            if (profile == null) { Log("profile refused: " + issues); return; }
            ctrl = ctx.CreateController(profile, "rw3-gip-proxy");
            var gate = new object();
            var c = ctrl;
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
            double rumL = 0, rumR = 0;
            long rumTick = 0;
            ctrl.OutputReceived += (_, pkt) =>
            {
                var msg = new byte[1 + pkt.Data.Length];
                msg[0] = pkt.ReportId;
                pkt.Data.Span.CopyTo(msg.AsSpan(1));
                Count("win→pad ", msg[0]);
                Trace("win→pad", msg);
                // the game's rumble on the way: [09][opt][seq][len][00 0F LT RT L R …], 0..100
                if (msg[0] == GipCodec.CmdRumble && msg.Length >= 10)
                {
                    rumL = msg[8] / 100.0; rumR = msg[9] / 100.0;
                    Interlocked.Exchange(ref rumTick, Environment.TickCount64);
                    lastRumble = Convert.ToHexString(msg);
                }
                transport.SendRawMessage(msg);
            };
            WinUsbGipTransport.ProxySink = ToWindows;
            Running = true;
            Log("clone created; resetting the pad so that Windows meets it from its announce.");
            transport.RequestPowerReset();
            while (true)
            {
                // the palms play the body rumble while it lasts (Windows sends rumble on change only)
                if (rumL > 0.01 || rumR > 0.01) XboxSensa.SetRumble(rumL, rumR);
                if (Environment.TickCount64 - Interlocked.Read(ref nextLog) >= 0)
                {
                    Interlocked.Exchange(ref nextLog, Environment.TickCount64 + 10_000);
                    Log($"10 s: {TakeCounts()}; last rumble {lastRumble}; pad {transport.Summary}");
                }
                if (!transport.IsOpen && !WinUsbGipTransport.Suspended) transport.TryOpen();
                Thread.Sleep(50);
            }
        }
        catch (Exception ex) { Log("stopped: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            WinUsbGipTransport.ProxySink = null;
            Running = false;
            try { ctrl?.Dispose(); } catch { }
        }
#else
        Log("built without HIDMaestro.");
#endif
    }

    private static long nextLog;
    private static int s_exactNoted, s_paddedNoted;
    private static string lastRumble = "-";
}
