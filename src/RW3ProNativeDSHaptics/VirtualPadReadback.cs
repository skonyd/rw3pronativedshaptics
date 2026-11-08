using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Reads the virtual DualSense's input reports the way a game does (shared HID handle, ReadFile),
/// so the bridge can compare what the game sees with the physical Xbox pad.
/// The virtual device is told apart from a physical DualSense by appearing after the bridge created it.
/// </summary>
public sealed class VirtualPadReadback : IDisposable
{
    public const string DualSenseUsbId = "vid_054c&pid_0ce6";
    public string? DevicePath { get; private set; }
    public long Reports => Interlocked.Read(ref _reports);
    public double LastReportMs { get; private set; } = double.NaN;

    private long _reports;
    private SafeFileHandle? _handle;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly Action<double, DualSenseInputState> _onReport;
    private readonly Func<double> _clockMs;

    public VirtualPadReadback(Action<double, DualSenseInputState> onReport, Func<double> clockMs)
    {
        _onReport = onReport; _clockMs = clockMs;
    }

    public static HashSet<string> DualSensePaths() =>
        SensaHidTransport.Hid.EnumeratePaths()
            .Where(p => p.Contains(DualSenseUsbId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits for a DualSense HID path that was not in <paramref name="before"/>; falls back to
    /// the only DualSense present. Returns false when nothing readable was found.</summary>
    public bool Start(HashSet<string> before, int waitMs = 5000)
    {
        var deadline = Environment.TickCount64 + waitMs;
        string? path = null;
        while (path == null && Environment.TickCount64 < deadline && !_stop)
        {
            var now = DualSensePaths();
            path = now.FirstOrDefault(p => !before.Contains(p) && Readable(p));
            if (path == null && now.Count == 1 && Environment.TickCount64 + 250 >= deadline && Readable(now.First())) path = now.First();
            if (path == null) Thread.Sleep(250);
        }
        if (path == null) return false;
        _handle = Open(path);
        if (_handle == null) return false;
        DevicePath = path;
        _stop = false;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "RW3-VirtualPadReadback" };
        _thread.Start();
        return true;
    }

    private static bool Readable(string path)
    {
        using var h = Open(path);
        return h != null && SensaHidTransport.Hid.InputLength(h) >= 11;
    }

    private static SafeFileHandle? Open(string path)
    {
        // Read access, shared: the game keeps its own handle and its own report queue.
        var h = SensaHidTransport.Hid.CreateFile(path, SensaHidTransport.Hid.GenericRead, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    private void ReadLoop()
    {
        var h = _handle!;
        int len = Math.Max(SensaHidTransport.Hid.InputLength(h), 64);
        var buf = new byte[len];
        while (!_stop)
        {
            if (!ReadFile(h, buf, len, out int read, IntPtr.Zero) || read <= 0) { if (_stop) break; Thread.Sleep(50); continue; }
            if (!DualSenseInputState.TryParseUsb(buf.AsSpan(0, read), out var s)) continue;
            double t = _clockMs();
            LastReportMs = t;
            Interlocked.Increment(ref _reports);
            _onReport(t, s);
        }
    }

    public void Stop()
    {
        _stop = true;
        var h = _handle;
        if (h is { IsInvalid: false, IsClosed: false }) CancelIoEx(h, IntPtr.Zero);
        _thread?.Join(500);
        _thread = null;
        h?.Dispose();
        _handle = null;
    }

    public void Dispose() => Stop();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int toRead, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
