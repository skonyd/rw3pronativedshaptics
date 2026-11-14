using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Captures only the sound of the app in the foreground (and its child processes) with Windows process loopback
/// (<c>VAD\Process_Loopback</c>, Windows 10 2004+), and follows the foreground: when another app comes to the front,
/// capture moves to it. RW3's own window and shell surfaces (taskbar, Start, alt-tab) keep the previous target
/// (<see cref="ForegroundAudioTarget"/>). Output: 48 kHz stereo float through the NAudio <see cref="IWaveIn"/>
/// events, so <see cref="AudioHapticsBridge"/> treats it like the system-wide loopback.
/// Nothing is recorded or stored; blocks go straight to the haptic analysis.
/// </summary>
public sealed class ForegroundAppCapture : IWaveIn
{
    private const uint LoopbackFlags = 0x00020000 /*LOOPBACK*/ | 0x00040000 /*EVENTCALLBACK*/ | 0x80000000 /*AUTOCONVERTPCM*/ | 0x08000000 /*SRC_DEFAULT_QUALITY*/;
    private const long BufferHns = 200_000;   // 20 ms
    private const int CheckForegroundMs = 500, RetryFailedMs = 5000;

    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Process name currently captured, or null while no app has been in the foreground yet.</summary>
    public string? TargetName => Volatile.Read(ref _targetName);
    private string? _targetName;

    private Thread? _worker;
    private volatile bool _stop;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly int _fixedTarget;

    /// <param name="fixedTargetPid">Diagnostics only: capture this process instead of following the foreground.</param>
    public ForegroundAppCapture(int fixedTargetPid = 0) => _fixedTarget = fixedTargetPid;

    public void StartRecording()
    {
        if (!ForegroundAudioTarget.Supported(Environment.OSVersion.Version.Build))
            throw new NotSupportedException("'Yalnız öndeki uygulama' Windows 10 2004 veya sonrasını gerektirir.");
        _stop = false;
        _worker = new Thread(Run) { IsBackground = true, Name = "RW3 foreground-app audio", Priority = ThreadPriority.AboveNormal };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public void StopRecording()
    {
        _stop = true;
        var w = _worker;
        if (w != null && w != Thread.CurrentThread) w.Join(2000);
        _worker = null;
    }

    public void Dispose() => StopRecording();

    private sealed class Session : IDisposable
    {
        public IAudioClientRaw Client = null!;
        public IAudioCaptureClientRaw Capture = null!;
        public AutoResetEvent Ready = new(false);
        public void Dispose()
        {
            try { Client?.Stop(); } catch { }
            if (Capture != null) Marshal.ReleaseComObject(Capture);
            if (Client != null) Marshal.ReleaseComObject(Client);
            Ready.Dispose();
        }
    }

    private void Run()
    {
        int target = 0, failedPid = 0;
        long nextCheck = 0, failedUntil = 0;
        Session? session = null;
        byte[] buffer = new byte[48000 * 8 / 5];   // 200 ms of stereo float
        try
        {
            while (!_stop)
            {
                long now = Environment.TickCount64;
                if (now >= nextCheck)
                {
                    nextCheck = now + CheckForegroundMs;
                    var (pid, name) = _fixedTarget > 0 ? (_fixedTarget, "pid " + _fixedTarget) : Foreground();
                    int next = _fixedTarget > 0 ? _fixedTarget : ForegroundAudioTarget.Next(target, name, pid, _ownPid);
                    if (next != target && !(next == failedPid && now < failedUntil))
                    {
                        session?.Dispose(); session = null;
                        try
                        {
                            session = Open(next);
                            target = next;
                            Volatile.Write(ref _targetName, name);
                            DiagnosticsService.Info("Audio haptics: foreground-app capture → " + name + " (pid " + next + ").");
                        }
                        catch (Exception ex)
                        {
                            failedPid = next; failedUntil = now + RetryFailedMs;
                            DiagnosticsService.Info("Audio haptics: cannot capture '" + name + "': " + ex.Message);
                        }
                    }
                }
                if (session == null) { Thread.Sleep(50); continue; }
                session.Ready.WaitOne(20);
                Drain(session, ref buffer);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Foreground-app capture", ex);
            session?.Dispose(); session = null;
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
            return;
        }
        session?.Dispose();
    }

    private void Drain(Session s, ref byte[] buffer)
    {
        while (true)
        {
            Check(s.Capture.GetNextPacketSize(out uint packet), "GetNextPacketSize");
            if (packet == 0) return;
            Check(s.Capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
            int bytes = (int)frames * 8;
            if (buffer.Length < bytes) buffer = new byte[bytes];
            if ((flags & 0x2) != 0 || data == IntPtr.Zero) Array.Clear(buffer, 0, bytes);   // AUDCLNT_BUFFERFLAGS_SILENT
            else Marshal.Copy(data, buffer, 0, bytes);
            s.Capture.ReleaseBuffer(frames);
            if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
        }
    }

    private Session Open(int pid)
    {
        var s = new Session();
        IntPtr pParams = IntPtr.Zero, pVariant = IntPtr.Zero, pFormat = IntPtr.Zero;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { TargetProcessId, INCLUDE_TARGET_PROCESS_TREE } }
            pParams = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(pParams, 0, 1);
            Marshal.WriteInt32(pParams, 4, pid);
            Marshal.WriteInt32(pParams, 8, 0);
            // PROPVARIANT VT_BLOB { cbSize, pBlobData }
            pVariant = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, 65);
            Marshal.WriteInt32(pVariant, 8, 12);
            Marshal.WriteIntPtr(pVariant, 16, pParams);

            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClientRaw).GUID, pVariant, handler, out var op);
            if (!handler.Done.Wait(3000)) throw new TimeoutException("ses arabirimi açılamadı (zaman aşımı)");
            op.GetActivateResult(out int hr, out object iface);
            Check(hr, "Activate");
            s.Client = (IAudioClientRaw)iface;

            // WAVEFORMATEX: IEEE float, 2 ch, 48 kHz (process loopback has no mix format; it converts to what we ask)
            pFormat = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(pFormat, 0, 3);
            Marshal.WriteInt16(pFormat, 2, 2);
            Marshal.WriteInt32(pFormat, 4, 48000);
            Marshal.WriteInt32(pFormat, 8, 48000 * 8);
            Marshal.WriteInt16(pFormat, 12, 8);
            Marshal.WriteInt16(pFormat, 14, 32);
            Marshal.WriteInt16(pFormat, 16, 0);
            Check(s.Client.Initialize(0, LoopbackFlags, BufferHns, 0, pFormat, IntPtr.Zero), "Initialize");
            Check(s.Client.SetEventHandle(s.Ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(s.Client.GetService(typeof(IAudioCaptureClientRaw).GUID, out object cap), "GetService");
            s.Capture = (IAudioCaptureClientRaw)cap;
            Check(s.Client.Start(), "Start");
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
        finally
        {
            if (pParams != IntPtr.Zero) Marshal.FreeHGlobal(pParams);
            if (pVariant != IntPtr.Zero) Marshal.FreeHGlobal(pVariant);
            if (pFormat != IntPtr.Zero) Marshal.FreeHGlobal(pFormat);
        }
    }

    private static (int Pid, string? Name) Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, null);
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return (0, null);
        try { using var p = Process.GetProcessById((int)pid); return ((int)pid, p.ProcessName); }
        catch { return (0, null); }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} 0x{hr:X8}");
    }

    // ── interop ──
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marker: the handler may be called from any thread (the activation completes on a worker thread).</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClientRaw
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClientRaw
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
