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
    /// <summary>Options byte for Razer's 0x0F command (to be confirmed on hardware; see GipCodec.Razer).</summary>
    public static volatile byte RazerOptions = 0x00;
    /// <summary>Set before a driver switch (and kept until RW3 restarts): no part of RW3 may open the pad's WinUSB handle
    /// again. On 2026-11-22 the Sensa engine reopened it while the instance was being uninstalled; with another app
    /// (Ubisoft Connect) also vetoing the removal, Windows' device manager locked up and shutdown hung for 10 minutes.</summary>
    public static volatile bool Suspended;

    /// <summary>Device paths of WinUSB-bound Razer GIP pads (our GUID first, then WinUSB's default).</summary>
    public static List<string> DevicePaths()
    {
        var list = new List<string>();
        foreach (var g in new[] { InterfaceGuid, DefaultWinUsbGuid })
            foreach (var p in InterfacePaths(g))
                if (p.Contains("vid_1532&pid_0a3f", StringComparison.OrdinalIgnoreCase) && !list.Contains(p)) list.Add(p);
        return list;
    }

    public static bool DevicePresent() => DevicePaths().Count > 0;

    /// <summary>Opens the first WinUSB Wolverine, starts the reader and powers the pad on. Safe to call repeatedly.</summary>
    public bool TryOpen()
    {
        lock (_write)
        {
            if (IsOpen) return true;
            if (Suspended) { State = "askıda (sürücü değişiyor)"; return false; }
            var paths = DevicePaths();
            if (paths.Count == 0) { State = "WinUSB'de Wolverine yok"; return false; }
            var f = CreateFile(paths[0], 0xC0000000, 3, IntPtr.Zero, 3, 0x40000080 /*OVERLAPPED|NORMAL*/, IntPtr.Zero);
            if (f.IsInvalid) { State = "açılamadı " + Marshal.GetLastWin32Error(); f.Dispose(); return false; }
            if (!WinUsb_Initialize(f, out var usb)) { State = "WinUsb_Initialize " + Marshal.GetLastWin32Error(); f.Dispose(); return false; }
            // no selective suspend: a suspended pad answered reads with error 22 (2026-11-22)
            byte off = 0;
            WinUsb_SetPowerPolicy(usb, 0x81 /*AUTO_SUSPEND*/, 1, ref off);
            uint timeout = 250;
            WinUsb_SetPipePolicy(usb, 0x81, 0x03 /*PIPE_TRANSFER_TIMEOUT*/, 4, ref timeout);
            uint wt = 100;
            WinUsb_SetPipePolicy(usb, 0x01, 0x03, 4, ref wt);
            _file = f; _usb = usb; _stop = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "RW3 direct mode GIP reader" };
            _reader.Start();
            Send(GipCodec.PowerOn(NextSeq()));
            State = "açık";
            Log("Direct mode: WinUSB GIP pipe open (" + paths[0] + "), power-on sent.");
            return true;
        }
    }

    private byte NextSeq() { byte s = _seq; _seq = (byte)(_seq % 255 + 1); return s; }

    private void ReadLoop()
    {
        var buf = new byte[512];
        int errors = 0, logged = 0;
        long nextSummary = Environment.TickCount64 + 10_000, opened = Environment.TickCount64;
        bool resetSent = false;
        while (!_stop)
        {
            // a pad that was already connected ignores power-on until it re-announces: reset it once (no input in 1.5 s)
            if (!resetSent && Interlocked.Read(ref _inputs) == 0 && Environment.TickCount64 - opened > 1500)
            {
                resetSent = true;
                Log("Direct mode: no input 1.5 s after open, sending GIP power reset (the pad re-announces).");
                Send(GipCodec.PowerReset(NextSeq()));
            }
            if (Environment.TickCount64 >= nextSummary)
            {
                nextSummary = Environment.TickCount64 + 10_000;
                Log("Direct mode: " + Summary + $", last pad btn 0x{_pad.Buttons:X4} LT {_pad.LeftTrigger} RT {_pad.RightTrigger} LX {_pad.LX}");
            }
            if (!WinUsb_ReadPipe(_usb, 0x81, buf, (uint)buf.Length, out uint n, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                if (e == 121 /*SEM_TIMEOUT*/) continue;
                if (errors == 0 || errors % 20 == 0) Log($"Direct mode: GIP read error {e} (#{errors + 1}), resetting the pipe.");
                WinUsb_ResetPipe(_usb, 0x81);
                // ~1 s of errors (unplugged, or the pad reset itself): close; the bridge reopens it within a second
                if (++errors > 20) { State = "okuma hatası " + e; Log("Direct mode: GIP read failed " + e + " 20 times, closing (reopen follows)."); break; }
                Thread.Sleep(50);
                continue;
            }
            errors = 0;
            foreach (var m in GipCodec.Decode(buf.AsSpan(0, (int)n)))
            {
                Interlocked.Increment(ref _messages);
                if (logged < 16 && (m.Command != GipCodec.CmdInput || logged < 12))
                {
                    logged++;
                    Log($"Direct mode: GIP in cmd 0x{m.Command:X2} opt 0x{m.Options:X2} seq {m.Sequence} len {m.Payload.Length}: {Convert.ToHexString(m.Payload.AsSpan(0, Math.Min(24, m.Payload.Length)))}");
                }
                if (m.WantsAck) Send(GipCodec.Ack(m));
                if (m.Command == 0x10 && m.Payload.Length >= 8 && Interlocked.Increment(ref _razerLogged) <= 10)
                    Log($"Direct mode: Razer reply status 0x{m.Payload[0]:X2} tx {m.Payload[1]} class 0x{m.Payload[6]:X2} cmd 0x{m.Payload[7]:X2}");
                switch (m.Command)
                {
                    case GipCodec.CmdAnnounce: Send(GipCodec.PowerOn(NextSeq())); break;   // (re)connected: power on again
                    case GipCodec.CmdGuide: _guide = GipCodec.GuidePressed(m.Payload); break;
                    case GipCodec.CmdInput:
                        if (GipCodec.TryParseInput(m.Payload, _guide, out var pad))
                        {
                            lock (_write) { _pad = pad; _padTick = Environment.TickCount64; }
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
        var msg = GipCodec.Rumble(NextSeq(), bodyLeft, bodyRight, triggerLeft, triggerRight);
        // 2026-11-22: the trigger part was felt, the body part not: log a few packets that carry body values
        if ((bodyLeft > .05 || bodyRight > .05) && Interlocked.Increment(ref _bodyLogged) <= 6)
            Log("Direct mode: GIP out rumble " + Convert.ToHexString(msg));
        return Send(msg);
    }
    private int _bodyLogged, _razerLogged;

    /// <summary>Razer's 60-byte 0x0F packet (Sensa frame, RGB, battery…), the same bytes WGI's SendMessage carried.</summary>
    public bool SendRazer(ReadOnlySpan<byte> packet60) => IsOpen && Send(GipCodec.Razer(NextSeq(), packet60, RazerOptions));

    private bool Send(byte[] message)
    {
        lock (_write)
        {
            if (_usb == IntPtr.Zero) return false;
            bool ok = WinUsb_WritePipe(_usb, 0x01, message, (uint)message.Length, out uint w, IntPtr.Zero) && w == message.Length;
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
            if (_usb != IntPtr.Zero) { try { Send(GipCodec.Rumble(NextSeq(), 0, 0, 0, 0)); } catch { } WinUsb_Free(_usb); _usb = IntPtr.Zero; }
            _file?.Dispose(); _file = null;
            State = "kapalı";
        }
    }

    public void Dispose() => Close();

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
