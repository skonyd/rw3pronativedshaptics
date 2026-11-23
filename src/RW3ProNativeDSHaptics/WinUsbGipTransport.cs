using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Direct mode (docs/WINUSB_DIRECT_MODE.md): the Wolverine on Microsoft's WinUSB driver instead of dc1-controller, and RW3
/// speaking GIP on its interrupt pipe (OUT 0x01 / IN 0x81, E-20261122-58). Nothing in Windows' controller stack sits in
/// between, so no focus gate: rumble with the trigger motors and Razer's Sensa frames reach the pad while a game is in
/// front. One shared instance; a reader thread keeps the latest input, ACKs what asks for it and powers the pad on.
/// </summary>
public sealed class WinUsbGipTransport : IDisposable
{
    /// <summary>Written as DeviceInterfaceGUIDs when direct mode is installed (DirectModeDriver), so RW3 finds the pad.</summary>
    public static readonly Guid InterfaceGuid = new("5B5B7A57-3E8D-4C2E-9A0B-523357444952");
    /// <summary>What WinUSB registers when the instance has no DeviceInterfaceGUIDs (fallback lookup).</summary>
    public static readonly Guid DefaultWinUsbGuid = new("DEE824EF-729B-4A0E-9C14-B7117D33A817");
    public static WinUsbGipTransport Shared { get; } = new();
    /// <summary>Where open/close events go (RW3 points it at DiagnosticsService; the contract tests leave it silent).</summary>
    public static Action<string> Log = _ => { };

    private SafeFileHandle? _file;
    private IntPtr _usb;
    // GIP interrupt pipes; read from the interface (cable: OUT 0x01 / IN 0x81, E-20261122-58; the wireless adapter's
    // descriptor was never read, so nothing is assumed for it)
    private byte _in = 0x81, _out = 0x01;
    private string _path = "";
    // with the cable and the adapter both on WinUSB, which one RW3 tries (moves on when one gives no input)
    private static int s_pathIndex;
    private static readonly HashSet<string> s_resetPaths = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _reader;
    private volatile bool _stop;
    private readonly object _write = new();
    private byte _seq = 1;
    private XInputPad _pad;
    private bool _guide;
    private long _padTick, _inputs, _messages, _writes, _writeFailures;
    public string State { get; private set; } = "kapalı";
    public bool IsOpen => _usb != IntPtr.Zero && !_stop;
    public long Inputs => Interlocked.Read(ref _inputs);
    public long Messages => Interlocked.Read(ref _messages);
    public long Writes => Interlocked.Read(ref _writes);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    /// <summary>Options byte for Razer's 0x0F command: 0x00 (Razer answers with 0x10 status 0x02 on cable and adapter,
    /// E-20261122-61 / E-20261123-68).</summary>
    public static volatile byte RazerOptions = 0x00;
    /// <summary>Set before a driver switch (and kept until RW3 restarts): no part of RW3 may open the pad's WinUSB handle
    /// again. On 2026-11-22 the Sensa engine reopened it while the instance was being uninstalled; with another app
    /// (Ubisoft Connect) also vetoing the removal, Windows' device manager locked up and shutdown hung for 10 minutes.</summary>
    public static volatile bool Suspended;
    /// <summary>This process is the login bridge (SYSTEM, LoginBridge): it owns the pad through DirectModeOwnership
    /// itself; RW3 in the user's session first asks the bridge to let go.</summary>
    public static volatile bool IsLoginBridge;
    /// <summary>GIP proxy (GipProxy, docs/research/GIP_VIRTUAL_PAD.md): Windows' Xbox driver is the pad's host through a
    /// virtual clone. While set, this transport stops acting as the host (no power on, LED, identify, ACKs, reset) and
    /// hands every message from the pad, except Razer's replies to RW3 (0x10), to the sink, byte for byte.</summary>
    public static volatile Action<byte[]>? ProxySink;
    public static bool ProxyMode => ProxySink != null;
    /// <summary>GIP proxy: one message from Windows, as it came.</summary>
    public bool SendRawMessage(byte[] message) => IsOpen && Send(message);
    /// <summary>GIP proxy: make the pad leave and announce again, so Windows meets it from the start.</summary>
    public bool RequestPowerReset() => IsOpen && Send(GipCodec.PowerReset(NextSeq()));

    /// <summary>Device paths of WinUSB-bound Razer GIP pads (our GUID first, then WinUSB's default).</summary>
    public static List<string> DevicePaths()
    {
        // One path per device: the cable registered under both GUIDs, and RW3 took the two paths for two connections,
        // switching between them and resetting the pad on each (2026-11-22 23:58, E-20261122-66).
        var list = new List<string>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in new[] { InterfaceGuid, DefaultWinUsbGuid })
            foreach (var p in InterfacePaths(g))
                if (p.Contains("vid_1532&pid_0a3f", StringComparison.OrdinalIgnoreCase) && devices.Add(DeviceKey(p))) list.Add(p);
        return list;
    }

    /// <summary>The device part of an interface path (<c>\\?\USB#VID_1532&amp;PID_0A3F#serial</c>), without the interface GUID.</summary>
    internal static string DeviceKey(string path)
    {
        int i = path.LastIndexOf('#');
        return i > 0 ? path[..i] : path;
    }

    public static bool DevicePresent() => DevicePaths().Count > 0;

    private static long s_presentAt = long.MinValue;
    private static volatile bool s_present;
    /// <summary><see cref="DevicePresent"/> refreshed at most once a second: the bridges ask on every loop pass
    /// (250 times a second), and each answer is two Configuration Manager queries.</summary>
    public static bool DevicePresentCached()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref s_presentAt) >= 1000) { s_present = DevicePresent(); Interlocked.Exchange(ref s_presentAt, now); }
        return s_present;
    }

    private readonly AutoResetEvent _inputArrived = new(false);
    private long _inputStamp;
    /// <summary>Signalled on every input report and Xbox-button message: a bridge waits on it instead of sleeping, so a
    /// press reaches the virtual pad at once (Thread.Sleep(4) can last a whole 15.6 ms timer tick).</summary>
    public WaitHandle InputArrived => _inputArrived;
    /// <summary>Stopwatch timestamp of the latest input (for the bridges' input-delay figure).</summary>
    public long LastInputStamp => Interlocked.Read(ref _inputStamp);

    /// <summary>Opens the first WinUSB Wolverine, starts the reader and powers the pad on. Safe to call repeatedly.</summary>
    public bool TryOpen()
    {
        if (IsOpen) return true;
        // RW3 in the user's session: take the pad from the login bridge first (WinUSB opens a device only once).
        // Outside the write lock: the bridge may need a few seconds to let go.
        if (!IsLoginBridge && !Suspended && Environment.IsPrivilegedProcess && DevicePresent() && !DirectModeOwnership.AcquireForSession())
        {
            State = "giriş ekranı köprüsü kolu bırakmadı";
            return false;
        }
        lock (_write)
        {
            if (IsOpen) return true;
            if (Suspended) { State = "askıda (sürücü değişiyor)"; return false; }
            // Only the elevated RW3 drives the pad: a non-elevated start hands over to the elevated one within a second,
            // and both opening the pipe reset the pad twice (2026-11-23, E-20261123-68). Without elevation there is no
            // virtual pad either, so a directly driven pad would reach no game.
            if (!Environment.IsPrivilegedProcess) { State = "yönetici olarak çalışan RW3 bekleniyor"; return false; }
            FreeHandles();   // the reader stopped (read errors / no input): WinUSB opens a device only once
            var paths = DevicePaths();
            if (paths.Count == 0) { State = "WinUSB'de Wolverine yok"; return false; }
            string path = paths[(int)((uint)s_pathIndex % (uint)paths.Count)];
            var f = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000080 /*OVERLAPPED|NORMAL*/, IntPtr.Zero);
            if (f.IsInvalid) { State = "açılamadı " + Marshal.GetLastWin32Error(); f.Dispose(); return false; }
            if (!WinUsb_Initialize(f, out var usb)) { State = "WinUsb_Initialize " + Marshal.GetLastWin32Error(); f.Dispose(); return false; }
            // no selective suspend: a suspended pad answered reads with error 22 (2026-11-22)
            byte off = 0;
            WinUsb_SetPowerPolicy(usb, 0x81 /*AUTO_SUSPEND*/, 1, ref off);
            var (pin, pout, pipes) = FindPipes(usb);
            _in = pin; _out = pout;
            uint timeout = 250;
            WinUsb_SetPipePolicy(usb, _in, 0x03 /*PIPE_TRANSFER_TIMEOUT*/, 4, ref timeout);
            uint wt = 100;
            WinUsb_SetPipePolicy(usb, _out, 0x03, 4, ref wt);
            _file = f; _usb = usb; _stop = false; _path = path;
            Log($"Direct mode: interface 0 pipes {pipes}; using IN 0x{_in:X2} OUT 0x{_out:X2}.");
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "RW3 direct mode GIP reader" };
            _reader.Start();
            if (!ProxyMode)   // with the GIP proxy, Windows is the host and powers the pad on itself
            {
                Send(GipCodec.PowerOn(NextSeq()));
                Send(GipCodec.LedOn(NextSeq()));
                ScheduleIdentify();
            }
            ScheduleConnectedLight();
            State = "açık";
            Log("Direct mode: WinUSB GIP pipe open (" + path + (paths.Count > 1 ? $", 1 of {paths.Count}" : "") + "), power-on sent.");
            return true;
        }
    }

    private byte NextSeq() { byte s = _seq; _seq = (byte)(_seq % 255 + 1); return s; }

    private void ReadLoop()
    {
        var buf = new byte[512];
        int errors = 0, logged = 0;
        long nextSummary = Environment.TickCount64 + 10_000, opened = Environment.TickCount64, lastLogged = 0;
        string lastSummary = "";
        // at most one reset per RW3 run: the reset makes the pad leave and rejoin USB, the pipe reopens, and a reset on
        // every reopen made it cycle endlessly (2026-11-22, Windows' connect/disconnect sound looping)
        bool resetSent;
        lock (s_resetPaths) resetSent = !s_resetPaths.Add(DeviceKey(_path));   // once per device and run
        long inputsAtOpen = Interlocked.Read(ref _inputs);
        while (!_stop)
        {
            // a pad that was already connected ignores power-on until it re-announces: reset it once (no input in 1.5 s)
            if (!resetSent && !ProxyMode && Interlocked.Read(ref _inputs) == inputsAtOpen && Environment.TickCount64 - opened > 1500)
            {
                resetSent = true;
                Log("Direct mode: no input 1.5 s after open, sending GIP power reset (the pad re-announces).");
                Send(GipCodec.PowerReset(NextSeq()));
            }
            // cable and adapter both on WinUSB: the one without the pad stays silent; try the other (the bridge reopens)
            if (Interlocked.Read(ref _inputs) == inputsAtOpen && Environment.TickCount64 - opened > 5000 && DevicePaths().Count > 1)
            {
                Log("Direct mode: no input 5 s on " + _path + ", trying the other Wolverine connection.");
                Interlocked.Increment(ref s_pathIndex);
                State = "diğer bağlantı deneniyor";
                break;
            }
            if (Environment.TickCount64 >= nextSummary)
            {
                nextSummary = Environment.TickCount64 + 10_000;
                // every 10 s while something happens, else once a minute (an idle pad repeated the same line)
                string summary = Summary;
                if (summary != lastSummary || Environment.TickCount64 - lastLogged >= 60_000)
                {
                    Log("Direct mode: " + summary + (RazerErrors > 0 ? $", Razer errors {RazerErrors}" : ""));
                    lastSummary = summary; lastLogged = Environment.TickCount64;
                }
            }
            if (!WinUsb_ReadPipe(_usb, _in, buf, (uint)buf.Length, out uint n, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                if (e == 121 /*SEM_TIMEOUT*/) continue;
                if (errors == 0 || errors % 20 == 0) Log($"Direct mode: GIP read error {e} (#{errors + 1}), resetting the pipe.");
                WinUsb_ResetPipe(_usb, _in);
                // ~1 s of errors (unplugged, or the pad reset itself): close; the bridge reopens it within a second
                if (++errors > 20) { State = "okuma hatası " + e; Log("Direct mode: GIP read failed " + e + " 20 times, closing (reopen follows)."); break; }
                Thread.Sleep(50);
                continue;
            }
            errors = 0;
            var sink = ProxySink;
            foreach (var (m, mStart, mLength) in GipCodec.DecodeSpans(buf.AsSpan(0, (int)n)))
            {
                Interlocked.Increment(ref _messages);
                if (sink != null)
                {
                    // GIP proxy: Windows answers, acks and powers the pad; RW3 only keeps the input state and its own Razer replies
                    if (m.Command != 0x10 && mStart + mLength <= n) { try { sink(buf.AsSpan(mStart, mLength).ToArray()); } catch { } }
                    if (m.Command is not (GipCodec.CmdInput or GipCodec.CmdGuide)) continue;
                }
                // connection messages only (announce, status, power…): input and Razer replies are counted, not logged
                if (logged < 8 && m.Command is not (GipCodec.CmdInput or 0x10))
                {
                    logged++;
                    Log($"Direct mode: GIP in cmd 0x{m.Command:X2} opt 0x{m.Options:X2} seq {m.Sequence} len {m.Payload.Length}: {Convert.ToHexString(m.Payload.AsSpan(0, Math.Min(24, m.Payload.Length)))}");
                }
                if (sink == null)
                {
                    if (m.Command == GipCodec.CmdIdentify) { HandleIdentify(m); continue; }   // chunked: acks itself
                    if (m.WantsAck) Send(GipCodec.Ack(m));
                }
                // Razer replies: status 0x02 = accepted; anything else is counted and the first one logged
                if (m.Command == 0x10 && m.Payload.Length >= 8 && m.Payload[0] != 0x02 && m.Payload[0] != 0x00 && Interlocked.Increment(ref _razerErrors) == 1)
                    Log($"Direct mode: Razer reply status 0x{m.Payload[0]:X2} class 0x{m.Payload[6]:X2} cmd 0x{m.Payload[7]:X2}");
                switch (m.Command)
                {
                    case GipCodec.CmdAnnounce:
                        SaveOnce(AnnounceFile, m.Payload, ref _announceSaved);   // for the GIP virtual pad (a faithful clone)
                        // like Windows (E-20261123-74): identify first, power on after it. Asked after power-on, the pad
                        // never answered (2026-11-23 12:00); without a request pending, power on at once as before.
                        if (!IsLoginBridge && IdentifyData == null && Interlocked.Exchange(ref _identAsked, 2) != 2)
                        {
                            Send(GipCodec.IdentifyRequest(NextSeq()));
                            Log("Direct mode: identify request sent after the announce.");
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                for (int w = 0; w < 10 && IdentifyData == null; w++) Thread.Sleep(50);
                                if (!IsOpen) return;
                                Send(GipCodec.PowerOn(NextSeq())); Send(GipCodec.LedOn(NextSeq())); ScheduleConnectedLight();
                            });
                        }
                        else { Send(GipCodec.PowerOn(NextSeq())); Send(GipCodec.LedOn(NextSeq())); ScheduleConnectedLight(); }   // (re)connected: power on, LED on
                        break;
                    case GipCodec.CmdGuide:
                        // The Xbox button comes in its own message; the next input report may be far away (nothing else
                        // moved), so the pad state takes it now. Waiting for 0x20 made the button work only sometimes.
                        _guide = GipCodec.GuidePressed(m.Payload);
                        lock (_write)
                        {
                            _pad = _pad with { Buttons = (ushort)(_guide ? _pad.Buttons | XInputPad.Guide : _pad.Buttons & ~XInputPad.Guide) };
                            _padTick = Environment.TickCount64;
                            _inputStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                            _inputArrived.Set();
                        }
                        break;
                    case GipCodec.CmdInput:
                        if (GipCodec.TryParseInput(m.Payload, _guide, out var pad))
                        {
                            lock (_write) { _pad = pad; _padTick = Environment.TickCount64; _inputStamp = System.Diagnostics.Stopwatch.GetTimestamp(); }
                            _inputArrived.Set();
                            Interlocked.Increment(ref _inputs);
                        }
                        break;
                }
            }
        }
        _stop = true;
    }

    /// <summary>The latest input; false once the pipe is closed (unplugged, read errors).</summary>
    public bool TryReadPad(out XInputPad pad)
    {
        // GIP pads report on change only: an idle pad keeps its last (neutral) state while the pipe is open
        lock (_write) { pad = _pad; return IsOpen; }
    }

    public bool Rumble(double bodyLeft, double bodyRight, double triggerLeft, double triggerRight)
    {
        if (!IsOpen) return false;
        return Send(GipCodec.Rumble(NextSeq(), bodyLeft, bodyRight, triggerLeft, triggerRight));
    }
    private long _razerErrors;
    public long RazerErrors => Interlocked.Read(ref _razerErrors);

    /// <summary>Razer's 60-byte 0x0F packet (Sensa frame, RGB, battery…), the same bytes WGI's SendMessage carried.</summary>
    public bool SendRazer(ReadOnlySpan<byte> packet60) => IsOpen && Send(GipCodec.Razer(NextSeq(), packet60, RazerOptions));

    private bool Send(byte[] message)
    {
        lock (_write)
        {
            if (_usb == IntPtr.Zero) return false;
            bool ok = WinUsb_WritePipe(_usb, _out, message, (uint)message.Length, out uint w, IntPtr.Zero) && w == message.Length;
            if (ok) Interlocked.Increment(ref _writes); else Interlocked.Increment(ref _writeFailures);
            return ok;
        }
    }

    public string Summary => $"{State}, girdi {Inputs}, mesaj {Messages}, yazım {Writes} (hata {WriteFailures})";

    public void Close()
    {
        _stop = true;
        try { _reader?.Join(600); } catch { }
        lock (_write)
        {
            if (_usb != IntPtr.Zero)
            {
                try { Send(GipCodec.Rumble(NextSeq(), 0, 0, 0, 0)); } catch { }
                // lights out: RW3 no longer drives the pad, and in direct mode nothing else does
                if (ConnectedLight) try { Send(GipCodec.Razer(NextSeq(), GipCodec.RazerBrightness(_razerTx++, 0), RazerOptions)); } catch { }
            }
            FreeHandles();
            State = "kapalı";
        }
    }

    public void Dispose() => Close();

    // ---- identify data (docs/research/GIP_VIRTUAL_PAD.md, question 2): read once, saved for the GIP virtual pad ----
    private byte[]? _identBuf;
    private int _identTotal, _identGot;
    private int _identAsked;
    /// <summary>The pad's identify data once read (null before).</summary>
    public byte[]? IdentifyData { get; private set; }
    public static string IdentifyFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RW3ProNativeDSHaptics", "gip-identify.bin");

    /// <summary>Asks the pad for its identify data once per RW3 run, 1.5 s after opening (the pad is powered on then).
    /// Read-only for the pad: it only answers.</summary>
    private void ScheduleIdentify()
    {
        if (IsLoginBridge || Interlocked.Exchange(ref _identAsked, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(3000);   // fallback when no announce came (a pad that was already on)
            if (!IsOpen) { Interlocked.Exchange(ref _identAsked, 0); return; }
            if (IdentifyData != null || Volatile.Read(ref _identAsked) == 2) return;
            bool ok = Send(GipCodec.IdentifyRequest(NextSeq()));
            Log("Direct mode: identify request " + (ok ? "sent" : "not sent") + ".");
        });
    }

    private readonly System.Collections.Generic.List<string> _identChunks = new();
    private int _announceSaved;
    public static string AnnounceFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(IdentifyFile)!, "gip-announce.bin");
    public static string IdentifyChunksFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(IdentifyFile)!, "gip-identify-chunks.txt");

    private void SaveOnce(string file, byte[] data, ref int flag)
    {
        if (IsLoginBridge || Interlocked.Exchange(ref flag, 1) == 1) return;
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); System.IO.File.WriteAllBytes(file, data); } catch { }
    }

    private void HandleIdentify(in GipMessage m)
    {
        var p = m.Payload;
        if (!m.Chunked)
        {
            if (m.WantsAck) Send(GipCodec.Ack(m));
            CompleteIdentify(p);
            return;
        }
        var (offset, start) = GipCodec.ChunkHeader(p);
        int len = p.Length - start;
        // how the pad chunks it (options, sequence, data length, offset): the GIP virtual pad sends its reply the same way
        _identChunks.Add($"{m.Options:X2} {m.Sequence} {len} {offset}");
        if ((m.Options & GipCodec.OptChunkStart) != 0)
        {
            if (offset <= 0 || offset > 65536) { Log($"Direct mode: identify start with odd length {offset}."); return; }
            _identTotal = offset; _identBuf = new byte[offset]; _identGot = 0;
            offset = 0;   // the first chunk's field is the total length; its data starts at 0
        }
        if (_identBuf == null) return;
        if (len > 0 && offset + len <= _identBuf.Length)
        {
            Array.Copy(p, start, _identBuf, offset, len);
            _identGot = Math.Max(_identGot, offset + len);
        }
        if (m.WantsAck) Send(GipCodec.ChunkAck(m, _identGot, Math.Max(0, _identTotal - _identGot)));
        if (len == 0 || _identGot >= _identTotal) CompleteIdentify(_identBuf);
    }

    private void CompleteIdentify(byte[] data)
    {
        if (IdentifyData != null) return;
        IdentifyData = data;
        if (_identChunks.Count > 0) Log("Direct mode: identify chunks (options seq length offset): " + string.Join(" | ", _identChunks));
        try { System.IO.File.WriteAllLines(IdentifyChunksFile, _identChunks); } catch { }
        Log($"Direct mode: identify data {data.Length} bytes: {Convert.ToHexString(data.AsSpan(0, Math.Min(64, data.Length)))}…");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IdentifyFile)!);
            System.IO.File.WriteAllBytes(IdentifyFile, data);
            Log("Direct mode: identify data saved to " + IdentifyFile + ".");
        }
        catch (Exception ex) { Log("Direct mode: identify data not saved: " + ex.Message); }
    }

    /// <summary>Dim white on the pad's RGB while RW3 drives it in direct mode, off when RW3 lets go: the user sees that
    /// it works (2026-11-23: in direct mode nothing lit the pad). Razer lighting in RAM only, profile 0.</summary>
    public static volatile bool ConnectedLight = true;
    public static byte ConnectedBrightness = 0x26;   // ~15 %
    private byte _razerTx = 0x40;
    private long _lightAt;

    private void ScheduleConnectedLight()
    {
        if (!ConnectedLight) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lightAt, now) < 2000) return;   // announces repeat every few ms
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(400);   // let the pad finish powering on
            if (!IsOpen) return;
            SendRazer(GipCodec.RazerBrightness(_razerTx++, ConnectedBrightness));
            Thread.Sleep(150);   // the firmware handles one lighting command per pass (WolverineLightingAndBattery)
            bool ok = SendRazer(GipCodec.RazerStatic(_razerTx++, 0xFF, 0xFF, 0xFF));
            Log("Direct mode: connected light (dim white) " + (ok ? "sent" : "not sent"));
        });
    }

    /// <summary>The direct-mode pad's buttons for RW3's own menus, left stick as D-pad; 0 while the pipe is closed.
    /// RW3's menus read XInput and skip RW3's own virtual pad, so in direct mode they saw no pad at all (2026-11-23).</summary>
    public ushort ButtonsForUi()
    {
        if (!IsOpen) return 0;
        XInputPad p;
        lock (_write) p = _pad;
        ushort b = p.Buttons;
        const short t = 20000;
        if (p.LX < -t) b |= XInputPad.DpadLeft;
        if (p.LX > t) b |= XInputPad.DpadRight;
        if (p.LY > t) b |= XInputPad.DpadUp;
        if (p.LY < -t) b |= XInputPad.DpadDown;
        return b;
    }

    private void FreeHandles()
    {
        if (_usb != IntPtr.Zero) { WinUsb_Free(_usb); _usb = IntPtr.Zero; }
        _file?.Dispose(); _file = null;
    }

    /// <summary>The interrupt IN/OUT pipes of interface 0 (GIP), or the cable's 0x81/0x01 when the query fails.</summary>
    private static (byte In, byte Out, string All) FindPipes(IntPtr usb)
    {
        byte pin = 0, pout = 0; var all = new List<string>();
        if (WinUsb_QueryInterfaceSettings(usb, 0, out var d))
            for (byte i = 0; i < d.bNumEndpoints; i++)
                if (WinUsb_QueryPipe(usb, 0, i, out var pi))
                {
                    all.Add($"0x{pi.PipeId:X2}/{pi.PipeType}/{pi.MaximumPacketSize}");
                    if (pi.PipeType != 3 /*interrupt*/) continue;
                    if ((pi.PipeId & 0x80) != 0) { if (pin == 0) pin = pi.PipeId; } else if (pout == 0) pout = pi.PipeId;
                }
        return (pin != 0 ? pin : (byte)0x81, pout != 0 ? pout : (byte)0x01, all.Count > 0 ? string.Join(" ", all) : "query failed");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterfaceDescriptor { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_QueryInterfaceSettings(IntPtr h, byte alt, out UsbInterfaceDescriptor d);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_QueryPipe(IntPtr h, byte alt, byte index, out WinUsbPipeInformation p);

    private static List<string> InterfacePaths(Guid g)
    {
        var list = new List<string>();
        if (CM_Get_Device_Interface_List_Size(out uint len, ref g, null, 0) != 0 || len <= 1) return list;
        var buf = new char[len];
        if (CM_Get_Device_Interface_List(ref g, null, buf, len, 0) != 0) return list;
        foreach (var p in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)) list.Add(p);
        return list;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_Interface_List_Size(out uint len, ref Guid g, string? dev, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_Interface_List(ref Guid g, string? dev, char[] buf, uint len, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetPipePolicy(IntPtr h, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetPowerPolicy(IntPtr h, uint policy, uint len, ref byte value);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_ResetPipe(IntPtr h, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_ReadPipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint read, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_WritePipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint written, IntPtr ov);
}
