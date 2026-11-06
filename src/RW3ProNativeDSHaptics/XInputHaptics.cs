using NAudio.Wave;
using NAudio.CoreAudioApi;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Threading.Timer;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>Facts observed by the app, NOT proof of motor motion or native HD haptics.</summary>
public sealed record CaptureSnapshot(
    bool IsCapturing, int XInputSlot, long Callbacks, long AudioFrames, long LastAudioTick,
    long LastActiveSoundTick, long OutputAccepted, long OutputRejected, long LastOutputTick,
    string? LastError, long LeftSoundTick, long RightSoundTick,
    long LeftOutputAcknowledged, long RightOutputAcknowledged, string? AudioEndpointId);

/// <summary>
/// Low-overhead WASAPI loopback to ordinary XInput rumble.
/// Does not acquire DualSense PCM or address Razer Sensa HD body actuators.
/// </summary>
public sealed class XInputHaptics : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed, RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    private readonly object _motorGate = new();
    private Timer? _silenceWatchdog;
    private int _motorNonzero;
    private int _preferredIndex = -1;
    public int PreferredIndex
    {
        get => Volatile.Read(ref _preferredIndex);
        set => Volatile.Write(ref _preferredIndex, value is >= -1 and <= 3 ? value : -1);
    }

    public int DetectTargetIndex()
    {
        int desired = PreferredIndex;
        if (desired < 0) return DetectIndex();
        try { return XInputGetState((uint)desired, out _) == 0 ? desired : -1; }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }

    private WasapiLoopbackCapture? _capture;
    private int _captureFlag, _selectedIndex = -1;
    private long _audioCallbacks, _audioFrames, _lastAudioTick, _lastActiveSoundTick;
    private long _outputAccepted, _outputRejected, _lastOutputTick, _lastUpdate;
    private string? _lastError;
    private string? _audioEndpointId;
    private long _leftSoundTick, _rightSoundTick, _leftOutputAck, _rightOutputAck;

    public static int DetectIndex()
    {
        try
        {
            for (uint index = 0; index < 4; index++)
                if (XInputGetState(index, out _) == 0) return (int)index;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return -1;
    }

    public CaptureSnapshot Snapshot() => new(
        Volatile.Read(ref _captureFlag) == 1, Volatile.Read(ref _selectedIndex),
        Interlocked.Read(ref _audioCallbacks), Interlocked.Read(ref _audioFrames),
        Interlocked.Read(ref _lastAudioTick), Interlocked.Read(ref _lastActiveSoundTick),
        Interlocked.Read(ref _outputAccepted), Interlocked.Read(ref _outputRejected),
        Interlocked.Read(ref _lastOutputTick), Volatile.Read(ref _lastError),
        Interlocked.Read(ref _leftSoundTick), Interlocked.Read(ref _rightSoundTick),
        Interlocked.Read(ref _leftOutputAck), Interlocked.Read(ref _rightOutputAck),
        Volatile.Read(ref _audioEndpointId));

    private bool SendMotor(int index, double strong, double weak)
    {
        lock (_motorGate)
        {
        if (index < 0) return false;
        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(Math.Clamp(strong, 0, 1) * ushort.MaxValue),
            RightMotorSpeed = (ushort)(Math.Clamp(weak, 0, 1) * ushort.MaxValue)
        };
        try
        {
            if (XInputSetState((uint)index, ref vibration) == 0)
            {
                Interlocked.Increment(ref _outputAccepted);
                Interlocked.Exchange(ref _lastOutputTick, Environment.TickCount64);
                if (strong > 0.025) Interlocked.Increment(ref _leftOutputAck);
                if (weak > 0.025) Interlocked.Increment(ref _rightOutputAck);
                Volatile.Write(ref _motorNonzero, strong > 0 || weak > 0 ? 1 : 0);
                return true;
            }
            Interlocked.Increment(ref _outputRejected);
            Interlocked.Exchange(ref _lastError, "Windows XInput titreşim komutunu reddetti.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Interlocked.Increment(ref _outputRejected);
            Interlocked.Exchange(ref _lastError, error.Message);
        }
        return false;
        }
    }

    /// <summary>Fail-safe stop even if WASAPI stops calling DataAvailable during silence.</summary>
    private void CheckAudioWatchdog()
    {
        if (!HapticSafetyRules.MustZeroStaleMotor(
                Volatile.Read(ref _captureFlag) == 1,
                Volatile.Read(ref _motorNonzero) == 1,
                Interlocked.Read(ref _lastAudioTick), Environment.TickCount64))
            return;
        lock (_motorGate)
        {
            if (HapticSafetyRules.MustZeroStaleMotor(
                    Volatile.Read(ref _captureFlag) == 1,
                    Volatile.Read(ref _motorNonzero) == 1,
                    Interlocked.Read(ref _lastAudioTick), Environment.TickCount64))
                SendMotor(Volatile.Read(ref _selectedIndex), 0, 0);
        }
    }

    /// <summary>Test one conventional XInput motor. Does not address Sensa VCM directly.</summary>
    public async Task<bool> TestChannelAsync(HapticTestChannel channel, CancellationToken token)
    {
        if (Volatile.Read(ref _captureFlag) == 1) return false;
        int index = DetectTargetIndex();
        if (index < 0) return false;
        bool acknowledged = false;
        try
        {
            acknowledged = channel == HapticTestChannel.Left
                ? SendMotor(index, 0.34, 0)
                : SendMotor(index, 0, 0.26);
            if (acknowledged) await Task.Delay(450, token);
        }
        finally { SendMotor(index, 0, 0); }
        return acknowledged;
    }

    /// <summary>Returns XInput API acknowledgment, not hardware sensor evidence.</summary>
    public async Task<bool> TestAsync()
    {
        if (Volatile.Read(ref _captureFlag) == 1) return false;
        int index = DetectTargetIndex();
        if (index < 0) return false;
        bool acknowledged = false;
        try
        {
            acknowledged = SendMotor(index, 0.30, 0.20);
            if (acknowledged) await Task.Delay(250);
        }
        finally { SendMotor(index, 0, 0); }
        return acknowledged;
    }

    public void StartAudio()
    {
        StopAudio();
        int index = DetectTargetIndex();
        if (index < 0) throw new InvalidOperationException(
            "XInput cihazı yok. Oyun kollarını kontrol edin; algılanan slot Wolverine olarak doğrulanmış değildir.");
        // Pin the test tone and captured system audio to the same output endpoint.
        using var enumerator = new MMDeviceEnumerator();
        using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string endpointId = defaultEndpoint.ID;
        var capture = new WasapiLoopbackCapture(defaultEndpoint);
        // WASAPI shared-mode frequently reports WAVEFORMATEXTENSIBLE rather than plain IEEE float.
        var format = capture.WaveFormat;
        bool float32 = format.BitsPerSample == 32 &&
            (format.Encoding == WaveFormatEncoding.IeeeFloat ||
             format is WaveFormatExtensible f32 &&
             f32.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        bool pcm16 = format.BitsPerSample == 16 &&
            (format.Encoding == WaveFormatEncoding.Pcm ||
             format is WaveFormatExtensible p16 &&
             p16.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));
        bool supported = float32 || pcm16;
        if (!supported)
        {
            capture.Dispose();
            throw new NotSupportedException("WASAPI ses biçimi desteklenmiyor (32-bit float veya 16-bit PCM gerekir).");
        }
        Interlocked.Exchange(ref _lastError, null);
        Interlocked.Exchange(ref _audioCallbacks, 0);
        Interlocked.Exchange(ref _audioFrames, 0);
        Interlocked.Exchange(ref _lastAudioTick, 0);
        Interlocked.Exchange(ref _lastActiveSoundTick, 0);
        Interlocked.Exchange(ref _outputAccepted, 0);
        Interlocked.Exchange(ref _outputRejected, 0);
        Interlocked.Exchange(ref _lastOutputTick, 0);
        Interlocked.Exchange(ref _lastUpdate, 0);
        Interlocked.Exchange(ref _leftSoundTick, 0);
        Interlocked.Exchange(ref _rightSoundTick, 0);
        Interlocked.Exchange(ref _leftOutputAck, 0);
        Interlocked.Exchange(ref _rightOutputAck, 0);
        Volatile.Write(ref _audioEndpointId, endpointId);
        Volatile.Write(ref _selectedIndex, index);
        _capture = capture;
        Volatile.Write(ref _captureFlag, 1);
        capture.DataAvailable += OnAudioData;
        capture.RecordingStopped += OnRecordingStopped;
        _silenceWatchdog = new Timer(
            static state => ((XInputHaptics)state!).CheckAudioWatchdog(),
            this, 100, 100);
        try { capture.StartRecording(); }
        catch
        {
            StopAudio();
            throw;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref _captureFlag) != 1) return;
        Volatile.Write(ref _captureFlag, 0);
        if (e.Exception != null)
            Interlocked.Exchange(ref _lastError, "Ses yakalama durdu: " + e.Exception.Message);
        else
            Interlocked.Exchange(ref _lastError, "WASAPI yakalama beklenmedik biçimde durdu.");
        try { SendMotor(Volatile.Read(ref _selectedIndex), 0, 0); } catch { }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (Volatile.Read(ref _captureFlag) != 1 || capture is null) return;
        var fmt = capture.WaveFormat;
        int bytes = fmt.BitsPerSample / 8;
        int frameBytes = fmt.Channels * bytes;
        if (frameBytes <= 0) return;
        int frames = e.BytesRecorded / frameBytes;
        if (frames == 0) return;
        Interlocked.Increment(ref _audioCallbacks);
        Interlocked.Add(ref _audioFrames, frames);
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastAudioTick, now);

        // Avoid per-sample allocations and expensive full-buffer processing.
        // At most ~256 samples regardless of audio sample rate or buffer size.
        int stride = Math.Max(1, (frames + 255) / 256);
        double leftEnergy = 0, rightEnergy = 0;
        double lastLeft = 0, lastRight = 0;
        double changeLeft = 0, changeRight = 0;
        int n = 0;
        for (int frame = 0; frame < frames; frame += stride)
        {
            int offset = frame * frameBytes;
            double left = fmt.BitsPerSample == 16
                ? BitConverter.ToInt16(e.Buffer, offset) / 32768.0
                : BitConverter.ToSingle(e.Buffer, offset);
            double right = fmt.Channels > 1
                ? (fmt.BitsPerSample == 16
                    ? BitConverter.ToInt16(e.Buffer, offset + bytes) / 32768.0
                    : BitConverter.ToSingle(e.Buffer, offset + bytes))
                : left;
            if (!double.IsFinite(left) || !double.IsFinite(right)) continue;
            left = Math.Clamp(left, -1, 1);
            right = Math.Clamp(right, -1, 1);
            leftEnergy += left * left;
            rightEnergy += right * right;
            changeLeft += Math.Abs(left - lastLeft);
            changeRight += Math.Abs(right - lastRight);
            lastLeft = left;
            lastRight = right;
            ++n;
        }
        if (n == 0) return;
        double rmsLeft = Math.Sqrt(leftEnergy / n);
        double rmsRight = Math.Sqrt(rightEnergy / n);
        if (Math.Max(rmsLeft, rmsRight) > 0.006)
            Interlocked.Exchange(ref _lastActiveSoundTick, now);
        // Evidence of distinct stereo system-audio channels, NOT native DualSense PCM.
        if (rmsLeft > 0.015 && rmsLeft > rmsRight * 3)
            Interlocked.Exchange(ref _leftSoundTick, now);
        if (rmsRight > 0.015 && rmsRight > rmsLeft * 3)
            Interlocked.Exchange(ref _rightSoundTick, now);

        // Bound processing & write frequency. At most 20 XInput commands/s.
        if (now - Interlocked.Read(ref _lastUpdate) < 50) return;
        Interlocked.Exchange(ref _lastUpdate, now);
        if (Volatile.Read(ref _captureFlag) != 1) return;
        lock (_motorGate)
        {
            // StopAudio must win over a callback already in flight.
            if (Volatile.Read(ref _captureFlag) != 1) return;
            SendMotor(Volatile.Read(ref _selectedIndex),
                Math.Min(0.36, rmsLeft * 2.5),
                Math.Min(0.25, rmsRight * 2.5));
        }
    }

    public void StopAudio()
    {
        Volatile.Write(ref _captureFlag, 0);
        Interlocked.Exchange(ref _silenceWatchdog, null)?.Dispose();
        var capture = _capture;
        _capture = null;
        int index = Volatile.Read(ref _selectedIndex);
        Volatile.Write(ref _selectedIndex, -1);
        Volatile.Write(ref _audioEndpointId, null);
        if (capture != null)
        {
            capture.DataAvailable -= OnAudioData;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        lock (_motorGate)
        {
            try { SendMotor(index, 0, 0); } catch { }
        }
    }

    public void Dispose() => StopAudio();
}
