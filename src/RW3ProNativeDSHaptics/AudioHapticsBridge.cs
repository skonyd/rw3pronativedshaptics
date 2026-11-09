using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Captures a playback device (WASAPI loopback, 10 ms request), takes its front L/R
/// (mono is duplicated), and converts supported PCM/Float32 to stereo 48 kHz. No recording.
///
/// Sync delay: loopback hears the mix BEFORE the output device. A Bluetooth speaker plays it
/// later (endpoint-dependent; the existing Bluetooth delay is a heuristic), so the
/// vibration would lead the sound. Audio blocks are held for <see cref="EffectiveDelayMs"/>
/// on a timeline and released by a pump thread, so the tail still plays when the sound stops
/// and loopback goes quiet.
/// </summary>
public sealed class AudioHapticsBridge : IDisposable
{
    private readonly IAudioHapticSink _sink;
    private WasapiCapture? _capture;
    private float[] _stereo = new float[8192];
    private float[] _raw = new float[8192];
    private SincResampler? _resampler;
    private long _lastSoundTick, _callbacks;

    public AudioHapticsBridge(IAudioHapticSink sink) { _sink = sink; }

    public void SetXboxOutputLevel(int percent)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            if (_delivery != null) _delivery.Configure(sink => { if (sink is XboxAudioSink x) x.OutputLevelPercent = HapticOutputLevel.Limit(percent); });
            else if (_sink is XboxAudioSink xbox) xbox.OutputLevelPercent = HapticOutputLevel.Limit(percent);
        }
    }

    /// <summary>App-wide setting: -1 = automatic (Bluetooth output -> <see cref="AudioSyncDelay.BluetoothAutoMs"/>, else 0), otherwise ms.</summary>
    public static int SyncDelaySetting = -1;

    /// <summary>Delay actually applied by the running capture (ms).</summary>
    public int EffectiveDelayMs { get; private set; }

    /// <summary>Overrides the app-wide setting for this capture (the on-screen sound test uses 0).</summary>
    public int? DelayOverrideMs { get; set; }
    public AudioHapticProfile Profile { get; init; }
    public bool OutputIsBluetooth { get; private set; }

    private readonly object _callbackGate = new();
    private readonly object _lifecycleGate = new();
    private bool _disposed;
    private AudioSinkDelivery? _delivery;
    private readonly AutoResetEvent _wake = new(false);
    private int _capturing;
    private int _maximumCallbackFrames;
    private string? _failure;
    public string? Failure => Volatile.Read(ref _failure);
    private Thread? _pump;
    private volatile bool _pumpStop;

    /// <summary>Bluetooth endpoints report enumerator "BTHENUM" (classic A2DP) or "BTHLE..." (LE Audio).</summary>
    public static bool IsBluetooth(MMDevice device)
    {
        try
        {
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);   // PKEY_Device_EnumeratorName
            var ps = device.Properties;
            if (!ps.Contains(key)) return false;
            string v = ps[key].Value?.ToString() ?? "";
            return v.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }


    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;
    public string SinkDescription => _sink.Description;
    public string? DeviceName { get; private set; }
    public long Callbacks => System.Threading.Interlocked.Read(ref _callbacks);
    /// <summary>
    /// Loopback carries the endpoint's SOFTWARE volume (measured 2026-11-07: Steam Streaming Speakers
    /// at -7.0 dB captured at 0.446 = 10^(-7/20)), so the vibration followed the speaker volume,
    /// unlike Native. When the endpoint has no hardware volume, the capture is divided by the current
    /// volume (tracked live); hardware-volume devices (e.g. Bluetooth absolute volume) are untouched.
    /// </summary>
    public float VolumeCompensation => _volumeComp;
    public bool CompensatesVolume { get; private set; }
    public const float MaxVolumeCompensation = 100f;   // -40 dB; below that the signal is mostly gone
    private volatile float _volumeComp = 1f;
    private MMDevice? _device;

    public static bool HasHardwareVolume(MMDevice device)
    {
        try { return (device.AudioEndpointVolume.HardwareSupport & EEndpointHardwareSupport.Volume) != 0; }
        catch { return false; }
    }

    private void UpdateVolumeCompensation()
    {
        var d = _device;
        if (d == null || !CompensatesVolume) { _volumeComp = 1f; return; }
        try
        {
            float db = d.AudioEndpointVolume.MasterVolumeLevel;
            float linear = MathF.Pow(10f, db / 20f);
            _volumeComp = linear > 1f / MaxVolumeCompensation ? 1f / linear : MaxVolumeCompensation;
        }
        catch { _volumeComp = 1f; }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data) => UpdateVolumeCompensation();

    public bool HasRecentSound => Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastSoundTick) < 2500;

    /// <summary>Starts capturing the default playback device, or <paramref name="deviceId"/> when given.</summary>
    public void Start(string? deviceId = null)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartCore(deviceId);
        }
    }

    private void StartCore(string? deviceId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId != null ? enumerator.GetDevice(deviceId)
                                      : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _device = device;
        WasapiCapture? capture = null;
        try
        {
            DeviceName = device.FriendlyName;
            OutputIsBluetooth = IsBluetooth(device);
            _resampler = null;
            CompensatesVolume = !HasHardwareVolume(device);
            UpdateVolumeCompensation();
            if (CompensatesVolume) { try { device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification; } catch { } }
            EffectiveDelayMs = AudioSyncDelay.Resolve(DelayOverrideMs ?? SyncDelaySetting, OutputIsBluetooth);
            capture = new ConfigurableWasapiLoopbackCapture(device, 10);
            var format = capture.WaveFormat;
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
                (format is WaveFormatExtensible p && p.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));
            if (format.Channels < 1 || format.SampleRate is < 8000 or > 192000 ||
                !(floating && format.BitsPerSample == 32 || pcm && format.BitsPerSample is 16 or 24 or 32))
                throw new NotSupportedException("Audio haptics requires PCM16/24/32 or Float32, 8–192kHz.");
            _maximumCallbackFrames = Math.Max(1, capture.WaveFormat.SampleRate / 4); // Reject bursts >250ms.
            _raw = new float[_maximumCallbackFrames * 2];
            _stereo = new float[48000]; // >250ms of stereo at 48k; no usual callback growth.
            if (format.SampleRate != 48000)
            {
                _resampler = new SincResampler(format.SampleRate, 48000);
                _resampler.Process(_raw, ref _stereo); // Allocate/warm coefficients and history outside WASAPI callback.
                _resampler.Reset();
            }
            _delivery = new AudioSinkDelivery(_sink, Profile);
            Volatile.Write(ref _failure, null);
            Interlocked.Exchange(ref _lastSoundTick, 0);
            Interlocked.Exchange(ref _callbacks, 0);
            _capture = capture;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            Volatile.Write(ref _capturing, 1);
            _pumpStop = false;
            _pump = new Thread(Pump) { IsBackground = true, Name = "Audio haptics delivery", Priority = ThreadPriority.BelowNormal };
            _pump.Start();
            capture.StartRecording();
        }
        catch
        {
            if (capture != null && !ReferenceEquals(_capture, capture)) capture.Dispose();
            Stop();
            throw;
        }
        DiagnosticsService.Info("Audio haptics: loopback of '" + DeviceName + "'" + (OutputIsBluetooth ? " (Bluetooth)" : "") +
                                " → " + _sink.Description + ", sync delay " + EffectiveDelayMs + " ms, volume " +
                                (CompensatesVolume ? "compensated x" + _volumeComp.ToString("F2") : "hardware (not in loopback)"));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_callbackGate)
        {
            if (!IsCapturing || !ReferenceEquals(sender, _capture)) return;
            try { ProcessData(sender, e); }
            catch (Exception ex) { Fail("Audio callback failed: " + ex.Message); }
        }
    }

    private void ProcessData(object? sender, WaveInEventArgs e)
    {
        var fmt = (sender as WasapiCapture)?.WaveFormat;
        if (fmt == null || e.BytesRecorded == 0) return;
        System.Threading.Interlocked.Increment(ref _callbacks);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible x && x.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        int bytes = fmt.BitsPerSample / 8, ch = Math.Max(1, fmt.Channels), frameBytes = bytes * ch;
        if (e.BytesRecorded % frameBytes != 0) throw new InvalidOperationException("Partial audio frame received.");
        int frames = e.BytesRecorded / frameBytes;
        if (frames > _maximumCallbackFrames) throw new InvalidOperationException("WASAPI burst exceeds 250ms processing bound.");
        float peak = 0;
        float comp = _volumeComp;
        for (int i = 0; i < frames; i++)
        {
            float rawL = Read(e.Buffer, i * frameBytes, bytes, isFloat);
            float rawR = ch > 1 ? Read(e.Buffer, i * frameBytes + bytes, bytes, isFloat) : rawL;
            float l = rawL * comp, r = rawR * comp;
            if (!float.IsFinite(l)) l = 0;
            if (!float.IsFinite(r)) r = 0;
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            _raw[2 * i] = l; _raw[2 * i + 1] = r;
        }
        int o;
        if (fmt.SampleRate == 48000)
        {
            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            Array.Copy(_raw, _stereo, frames * 2); o = frames * 2;
        }
        else
        {
            o = (_resampler ?? throw new InvalidOperationException("Audio converter not initialized."))
                .Process(_raw.AsSpan(0, frames * 2), ref _stereo);
        }
        if (peak > 0.003f) System.Threading.Interlocked.Exchange(ref _lastSoundTick, Environment.TickCount64);
        _delivery?.Enqueue(_stereo.AsSpan(0, o), Environment.TickCount64, EffectiveDelayMs);
        _wake.Set();
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _capturing, 0) == 0) return;
        Volatile.Write(ref _failure, message);
        try { _delivery?.Dispose(); } catch (Exception ex) { DiagnosticsService.Error("Audio failure silence", ex); }
        _wake.Set();
        DiagnosticsService.Info(message);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (_callbackGate)
        {
            if (ReferenceEquals(sender, _capture)) Fail("WASAPI capture stopped: " + (e.Exception?.Message ?? "endpoint ended"));
        }
    }

    private void Pump()
    {
        while (!_pumpStop)
        {
            try { _delivery?.Pump(Environment.TickCount64); }
            catch (Exception ex) { Fail("Audio output failed: " + ex.Message); }
            _wake.WaitOne(10); // Independent watchdog also runs while WASAPI sends no callbacks.
        }
    }

    private static float Read(byte[] b, int offset, int bytes, bool isFloat) =>
        isFloat && bytes == 4 ? BitConverter.ToSingle(b, offset)
        : bytes == 2 ? BitConverter.ToInt16(b, offset) / 32768f
        : bytes == 3 ? ((b[offset] | (b[offset + 1] << 8) | ((sbyte)b[offset + 2] << 16)) / 8388608f)
        : bytes == 4 ? BitConverter.ToInt32(b, offset) / 2147483648f
        : 0f;

    public void Stop()
    {
        lock (_lifecycleGate) { if (!_disposed) StopCore(); }
    }

    private void StopCore()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_callbackGate)
        {
            Volatile.Write(ref _capturing, 0);
            capture = _capture;
            _capture = null;
            device = _device;
            _device = null;
            try { _delivery?.Dispose(); } // Serialized with Accept; no later writes after Stop returns.
            catch (Exception ex) { DiagnosticsService.Error("Audio delivery stop", ex); }
            _pumpStop = true;
            _wake.Set();
        }
        if (capture != null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try { capture.StopRecording(); } catch (Exception ex) { DiagnosticsService.Error("Audio capture stop", ex); }
            try { capture.Dispose(); } catch (Exception ex) { DiagnosticsService.Error("Audio capture dispose", ex); }
        }
        if (device != null)
        {
            if (CompensatesVolume) { try { device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { } }
            try { device.Dispose(); } catch (Exception ex) { DiagnosticsService.Error("Audio device dispose", ex); }
        }
        _pump?.Join();
        _pump = null;
        _delivery = null;
        try { _sink.Silence(); } catch (Exception ex) { DiagnosticsService.Error("Audio sink silence", ex); }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            try { StopCore(); }
            finally { _disposed = true; _wake.Dispose(); }
        }
    }
}
