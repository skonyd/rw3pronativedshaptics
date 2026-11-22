using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Experiment (2026-11-22): GIP messages written straight to the Xbox GIP driver's control device <c>\\.\XboxGIP</c>, below
/// GameInputSvc / Windows.Gaming.Input, where the controller focus gate drops RW3's background output while another
/// controller app is in front (E-20261122-52…55). Mechanism from xbledctl (MIT, github.com/Leclowndu93150/xbledctl):
/// open the device, IOCTL 0x40001CD0 (re-enumerate: the handle then receives device announces), read announces
/// (command 0x01/0x02) for the device id, then WriteFile a 20-byte header + payload:
/// <c>[deviceId u64][command][flags][sequence][0][length u32][0 u32][payload…]</c>.
/// Rumble = GIP command 0x09: <c>[0x00, enable 0x0F, LT, RT, left, right, duration 0xFF, delay 0x00, repeat 0xEB]</c>,
/// magnitudes 0..100 (the same layout Windows wrote to RW3's virtual pad, E-20261122-17).
/// </summary>
public sealed class XboxGipRawOutput : IDisposable
{
    private const uint GipReenumerate = 0x40001CD0;
    private SafeFileHandle? _h;
    private ulong _deviceId;
    private byte _seq = 1;
    private readonly object _gate = new();
    public string State { get; private set; } = "-";
    public string Description { get; private set; } = "";
    public long Writes { get; private set; }
    public long Failures { get; private set; }
    public bool Ready => _h is { IsInvalid: false } && _deviceId != 0;

    /// <summary>Opens \\.\XboxGIP and finds the Razer pad's GIP device id from the announces (blocks up to ~3 s).</summary>
    public bool Open()
    {
        lock (_gate)
        {
            if (Ready) return true;
            var sb = new StringBuilder();
            try
            {
                _h = CreateFile(@"\\.\XboxGIP", 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
                if (_h.IsInvalid) { State = "XboxGIP açılamadı " + Marshal.GetLastWin32Error(); Description = State; _h = null; return false; }
                DeviceIoControl(_h, GipReenumerate, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                var ids = new List<(ulong Id, bool Razer, string Hex)>();
                var buf = new byte[4096];
                for (int i = 0; i < 24; i++)
                {
                    int n = ReadOverlapped(buf, 250);
                    if (n < 20) { if (n < 0) break; continue; }
                    ulong id = BitConverter.ToUInt64(buf, 0);
                    byte cmd = buf[8];
                    string hex = Convert.ToHexString(buf, 0, Math.Min(n, 48));
                    if (cmd is 0x01 or 0x02)
                    {
                        // the announce payload carries the USB vendor id; Razer = 0x1532 (bytes 32 15)
                        bool razer = false;
                        for (int k = 20; k + 1 < n; k++) if (buf[k] == 0x32 && buf[k + 1] == 0x15) { razer = true; break; }
                        if (!ids.Exists(x => x.Id == id)) ids.Add((id, razer, hex));
                    }
                    sb.Append($"[cmd 0x{cmd:X2} id {id:X16} len {n}] ");
                }
                var pick = ids.Find(x => x.Razer);
                if (pick.Id == 0 && ids.Count == 1) pick = ids[0];
                _deviceId = pick.Id;
                Description = $"{ids.Count} GIP device(s): " + string.Join(", ", ids.ConvertAll(x => $"{x.Id:X16}{(x.Razer ? " (Razer)" : "")}")) + " · " + sb;
                State = _deviceId != 0 ? $"XboxGIP ok, pad {_deviceId:X16}" : "XboxGIP açık, Wolverine bulunamadı";
                return _deviceId != 0;
            }
            catch (Exception ex) { State = "XboxGIP " + ex.Message; return false; }
        }
    }

    /// <summary>Standard GIP rumble (0x09) with the trigger motors; values 0..1.</summary>
    public bool Rumble(double bodyLeft, double bodyRight, double triggerLeft, double triggerRight)
    {
        if (!Ready) return false;
        static byte Q(double v) => (byte)Math.Round((double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0) * 100);
        return Send(0x09, 0x00, new byte[] { 0x00, 0x0F, Q(triggerLeft), Q(triggerRight), Q(bodyLeft), Q(bodyRight), 0xFF, 0x00, 0xEB });
    }

    /// <summary>Any GIP command to the pad (e.g. Razer's 0x0F vendor message later).</summary>
    public bool Send(byte command, byte flags, byte[] payload)
    {
        lock (_gate)
        {
            if (!Ready) return false;
            var pkt = new byte[20 + payload.Length];
            BitConverter.TryWriteBytes(pkt.AsSpan(0, 8), _deviceId);
            pkt[8] = command; pkt[9] = flags; pkt[10] = _seq; pkt[11] = 0;
            BitConverter.TryWriteBytes(pkt.AsSpan(12, 4), (uint)payload.Length);
            payload.CopyTo(pkt, 20);
            _seq = (byte)(_seq % 255 + 1);
            bool ok = WriteOverlapped(pkt, 500);
            if (ok) Writes++; else { Failures++; State = "XboxGIP yazma hatası " + Marshal.GetLastWin32Error(); }
            return ok;
        }
    }

    private int ReadOverlapped(byte[] buf, int timeoutMs)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            unsafe
            {
                if (ReadFile(_h!, pin.AddrOfPinnedObject(), (uint)buf.Length, out uint n, &ov)) return (int)n;
                if (Marshal.GetLastWin32Error() != 997 /*IO_PENDING*/) return -1;
                if (!ev.WaitOne(timeoutMs)) { CancelIoEx(_h!, &ov); GetOverlappedResult(_h!, &ov, out _, true); return 0; }
                return GetOverlappedResult(_h!, &ov, out n, false) ? (int)n : -1;
            }
        }
        finally { pin.Free(); }
    }

    private bool WriteOverlapped(byte[] pkt, int timeoutMs)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        var pin = GCHandle.Alloc(pkt, GCHandleType.Pinned);
        try
        {
            unsafe
            {
                if (WriteFile(_h!, pin.AddrOfPinnedObject(), (uint)pkt.Length, out uint n, &ov)) return n == pkt.Length;
                if (Marshal.GetLastWin32Error() != 997) return false;
                if (!ev.WaitOne(timeoutMs)) { CancelIoEx(_h!, &ov); GetOverlappedResult(_h!, &ov, out _, true); return false; }
                return GetOverlappedResult(_h!, &ov, out n, false) && n == pkt.Length;
            }
        }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        try { if (Ready) Rumble(0, 0, 0, 0); } catch { }
        lock (_gate) { _h?.Dispose(); _h = null; _deviceId = 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inLen, IntPtr outBuf, uint outLen, out uint returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(SafeFileHandle h, IntPtr buf, uint len, out uint read, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(SafeFileHandle h, IntPtr buf, uint len, out uint written, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out uint n, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
}
