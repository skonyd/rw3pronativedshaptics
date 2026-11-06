using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// EXPLICIT UAC 4-channel source only: host audio ch1/2, haptic audio ch3/4.
/// Does not manufacture a virtual DualSense or redirect a game's output.
/// A game must actually write authored haptic audio to the chosen device.
/// </summary>
public sealed class NativeDualSenseCapture : IDisposable
{
    private readonly object _captureGate = new();
    private WasapiLoopbackCapture? _capture;
    private FourChannelHapticAnalyzer.PcmEncoding _encoding;
    private SensaStereoPrototype? _sink;
    private HapticStereoRecorder? _recording;
    private RealtimeStereoPcmTransport? _realtimeTransport;
    private long _frames, _lastFrameTick, _leftEnergyFrames, _rightEnergyFrames;
    private long _lastLeftEnergyTick, _lastRightEnergyTick;
    private int _leftRmsBits, _rightRmsBits, _speakerRmsBits, _leftPeakBits, _rightPeakBits;
    private int _capturing;
    private string _status = "Not started";
    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;
    public long CapturedFrames => Interlocked.Read(ref _frames);
    public long LastFrameTick => Interlocked.Read(ref _lastFrameTick);
    public long LeftActiveBlocks => Interlocked.Read(ref _leftEnergyFrames);
    public long RightActiveBlocks => Interlocked.Read(ref _rightEnergyFrames);
    public long LastLeftEnergyTick => Interlocked.Read(ref _lastLeftEnergyTick);
    public long LastRightEnergyTick => Interlocked.Read(ref _lastRightEnergyTick);
    public float LeftRms => BitConverter.Int32BitsToSingle(Volatile.Read(ref _leftRmsBits));
    public float RightRms => BitConverter.Int32BitsToSingle(Volatile.Read(ref _rightRmsBits));
    public float SpeakerRms => BitConverter.Int32BitsToSingle(Volatile.Read(ref _speakerRmsBits));
    public float LeftPeak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _leftPeakBits));
    public float RightPeak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _rightPeakBits));
    public long InvalidSamples => Interlocked.Read(ref _invalidSamples);
    private long _invalidSamples;
    public string Status => Volatile.Read(ref _status);
    public string? EndpointId { get; private set; }

    public HapticPcmFormat CurrentPcmFormat
    {
        get
        {
            lock (_captureGate)
            {
                if (!IsCapturing || _capture == null)
                    throw new InvalidOperationException("Önce dört kanallı kaynak izlemesini başlat.");
                return _encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16
                    ? HapticPcmFormat.Signed16 : HapticPcmFormat.Float32;
            }
        }
    }

    // This link only exposes the ORIGINAL 48kHz rear PCM bytes to a
    // bounded, local, explicitly started discard/diagnostics probe.
    // It never opens Sensa hardware or calls Submit() on a vendor sink.
    public void AttachTransport(RealtimeStereoPcmTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_captureGate)
        {
            if (!IsCapturing || _capture == null || _sink != null ||
                _recording != null || _realtimeTransport != null)
                throw new InvalidOperationException(
                    "PCM aktarım provası sadece aktif ve boş source-only izlemede açılabilir.");
            var expected = _encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16
                ? HapticPcmFormat.Signed16 : HapticPcmFormat.Float32;
            if (transport.Format != expected)
                throw new InvalidOperationException("Stereo PCM taşımada veri formatı uyuşmuyor.");
            _realtimeTransport = transport;
        }
    }

    public void DetachTransport()
    {
        lock (_captureGate)
        {
            _realtimeTransport?.Reset();
            _realtimeTransport = null;
        }
    }

    public bool IsPcmRecording
    {
        get { lock (_captureGate) return _recording != null; }
    }

    public int PcmRecordingFrames
    {
        get { lock (_captureGate) return _recording?.CapturedFrames ?? 0; }
    }

    public bool PcmRecordingFull
    {
        get { lock (_captureGate) return _recording?.IsFull == true; }
    }

    public bool PcmRecordingTimedOut
    {
        get { lock (_captureGate) return _recording?.IsTimedOut == true; }
    }

    /// <summary>
    /// Capture only, never on a live Razer sink. Does not affect the USB
    /// DualSense original rendering/driver pipeline.
    /// </summary>
    public void BeginPcmRecording(int seconds = 10)
    {
        lock (_captureGate)
        {
            if (!IsCapturing || _capture == null || _sink != null)
                throw new InvalidOperationException(
                    "WAV kayıt için yalnızca aktif 4ch source-only izleme kullanılabilir.");
            if (_recording != null || _realtimeTransport != null)
                throw new InvalidOperationException(
                    "WAV kaydı ve gerçek zamanlı PCM taşıma provası aynı anda kullanılamaz.");
            var format = _encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16
                ? HapticPcmFormat.Signed16
                : HapticPcmFormat.Float32;
            _recording = new HapticStereoRecorder(format, seconds);
        }
    }

    public byte[] FinishPcmRecording()
    {
        lock (_captureGate)
        {
            var recorder = _recording ??
                throw new InvalidOperationException("Aktif kayıt bulunamadı.");
            _recording = null;
            return recorder.End();
        }
    }

    public void AbandonPcmRecording()
    {
        lock (_captureGate)
        {
            _recording?.Abandon();
            _recording = null;
        }
    }

    public sealed record EndpointCandidate(
        string Id, string Name, int Channels, int SampleRate, int BitsPerSample, bool CanCapture);

    private static FourChannelHapticAnalyzer.PcmEncoding? ResolveEncoding(WaveFormat fmt)
    {
        bool float32 = fmt.BitsPerSample == 32 &&
            (fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
            fmt is WaveFormatExtensible ef &&
            ef.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        bool pcm16 = fmt.BitsPerSample == 16 &&
            (fmt.Encoding == WaveFormatEncoding.Pcm ||
            fmt is WaveFormatExtensible ep &&
            ep.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));
        if (float32) return FourChannelHapticAnalyzer.PcmEncoding.Float32;
        if (pcm16) return FourChannelHapticAnalyzer.PcmEncoding.Pcm16;
        return null;
    }

    public static IReadOnlyList<EndpointCandidate> EnumeratePossibleSources()
    {
        var results = new List<EndpointCandidate>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            // Candidates are format-qualified, not certified DualSense devices.
            try
            {
                WaveFormat fmt = device.AudioClient.MixFormat;
                var encoding = ResolveEncoding(fmt);
                bool supported = encoding.HasValue &&
                    FourChannelHapticAnalyzer.IsCompatible(
                        fmt.Channels, fmt.SampleRate, fmt.BitsPerSample, encoding.Value);
                results.Add(new EndpointCandidate(
                    device.ID, device.FriendlyName, fmt.Channels, fmt.SampleRate, fmt.BitsPerSample, supported));
            }
            catch { /* Stale or unavailable endpoint is skipped without crashing the UI. */ }
        }
        return results;
    }

    // A source-only probe can run without any Razer SDK DLL or controller.
    public void Start(string endpointId, SensaStereoPrototype? sink = null)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        if (device.State != DeviceState.Active)
            throw new InvalidOperationException("Chosen DualSense/UAC endpoint is not active.");
        var capture = new WasapiLoopbackCapture(device);
        var fmt = capture.WaveFormat;
        var encoding = ResolveEncoding(fmt);
        // Never accept ordinary stereo system sound as native input.
        if (!encoding.HasValue ||
            !FourChannelHapticAnalyzer.IsCompatible(fmt.Channels, fmt.SampleRate, fmt.BitsPerSample, encoding.Value))
        {
            capture.Dispose();
            throw new NotSupportedException(
                $"Expected a four-channel 48 kHz DualSense UAC endpoint; selected endpoint reports " +
                $"{fmt.Channels} channels, {fmt.SampleRate} Hz, {fmt.Encoding}, {fmt.BitsPerSample} bits. " +
                "Do NOT treat default stereo system audio as Native DualSense haptic.");
        }
        if (sink != null && !sink.IsActive)
        {
            capture.Dispose();
            throw new InvalidOperationException("Sensa output provider is not active; no silent XInput fallback.");
        }
        lock (_captureGate)
        {
            _sink = sink;
            _encoding = encoding.Value;
        }
        EndpointId = endpointId;
        Volatile.Write(ref _leftRmsBits, 0);
        Volatile.Write(ref _rightRmsBits, 0);
        Volatile.Write(ref _speakerRmsBits, 0);
        Volatile.Write(ref _leftPeakBits, 0);
        Volatile.Write(ref _rightPeakBits, 0);
        Interlocked.Exchange(ref _invalidSamples, 0);
        Interlocked.Exchange(ref _frames, 0);
        Interlocked.Exchange(ref _lastFrameTick, 0);
        Interlocked.Exchange(ref _leftEnergyFrames, 0);
        Interlocked.Exchange(ref _rightEnergyFrames, 0);
        Interlocked.Exchange(ref _lastLeftEnergyTick, 0);
        Interlocked.Exchange(ref _lastRightEnergyTick, 0);
        lock (_captureGate) _capture = capture;
        capture.DataAvailable += OnAudio;
        capture.RecordingStopped += OnStopped;
        Volatile.Write(ref _capturing, 1);
        try
        {
            capture.StartRecording();
            Volatile.Write(ref _status,
                sink == null
                ? "SOURCE-ONLY 4ch UAC monitor; no Razer output; game authorship unverified"
                : "Four-channel UAC → experimental Sensa provider; game authorship unverified");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        var capture = _capture;
        if (!IsCapturing || capture == null) return;
        // Decode with the same deterministic analyzer used by CI: 4ch source identity
        // stays separate from speaker audio and from physical provider evidence.
        var metrics = FourChannelHapticAnalyzer.Analyze(
            args.Buffer.AsSpan(0, args.BytesRecorded), _encoding);
        if (metrics.ReceivedFrames == 0) return;
        lock (_captureGate)
        {
            // A callback that started before Stop/endpoint-switch may finish later.
            // Only the current capture can update telemetry or re-energize the sink.
            if (!IsCapturing || !ReferenceEquals(sender, _capture)) return;
            if (_realtimeTransport != null)
            {
                // Only full-size original rear ch3/4 PCM frames reach the
                // bounded transport; never normalized/analyzed RMS samples.
                int frameBytes = _encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16 ? 8 : 16;
                _realtimeTransport.WriteFourChannel(
                    args.Buffer.AsSpan(0, metrics.ReceivedFrames * frameBytes));
            }
            if (_recording != null)
            {
                // Correct full-frame suffix is saved independently of our
                // lossy RMS analyzer. Never disk-write in the audio callback.
                int frameBytes = _encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16 ? 8 : 16;
                _recording.Accept(args.Buffer.AsSpan(0, metrics.ReceivedFrames * frameBytes));
            }
            Interlocked.Add(ref _frames, metrics.ReceivedFrames);
            Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
            Interlocked.Add(ref _invalidSamples, metrics.NonFiniteSamples);
            Volatile.Write(ref _leftRmsBits, BitConverter.SingleToInt32Bits(metrics.LeftRms));
            Volatile.Write(ref _rightRmsBits, BitConverter.SingleToInt32Bits(metrics.RightRms));
            Volatile.Write(ref _speakerRmsBits, BitConverter.SingleToInt32Bits(metrics.SpeakerRms));
            Volatile.Write(ref _leftPeakBits, BitConverter.SingleToInt32Bits(metrics.LeftPeak));
            Volatile.Write(ref _rightPeakBits, BitConverter.SingleToInt32Bits(metrics.RightPeak));
            float la = metrics.LeftRms, ra = metrics.RightRms;
            if (la > 0.01f)
            {
                Interlocked.Increment(ref _leftEnergyFrames);
                Interlocked.Exchange(ref _lastLeftEnergyTick, Environment.TickCount64);
            }
            if (ra > 0.01f)
            {
                Interlocked.Increment(ref _rightEnergyFrames);
                Interlocked.Exchange(ref _lastRightEnergyTick, Environment.TickCount64);
            }
            // Any sink is independent of capture. Null means analysis-only,
            // not a fallback to system sound or conventional XInput.
            _sink?.Submit(Math.Min(0.33f, la * 2.5f), Math.Min(0.33f, ra * 2.5f));
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        lock (_captureGate)
        {
            if (!ReferenceEquals(sender, _capture)) return;
            Volatile.Write(ref _capturing, 0);
            _sink?.Submit(0, 0);
            ClearSignalMetrics();
        }
        Volatile.Write(ref _status,
            args.Exception is null ? "Audio capture unexpectedly stopped" :
            "Audio capture stopped: " + args.Exception.Message);
    }

    private void ClearSignalMetrics()
    {
        Volatile.Write(ref _leftRmsBits, 0);
        Volatile.Write(ref _rightRmsBits, 0);
        Volatile.Write(ref _speakerRmsBits, 0);
        Volatile.Write(ref _leftPeakBits, 0);
        Volatile.Write(ref _rightPeakBits, 0);
    }

    public NativeCaptureState CurrentState => NativeCaptureHealth.Evaluate(
        IsCapturing, LastFrameTick, LastLeftEnergyTick, LastRightEnergyTick,
        Environment.TickCount64);

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_captureGate)
        {
            // Stop wins even if DataAvailable has already decoded a buffer.
            Volatile.Write(ref _capturing, 0);
            _sink?.Submit(0, 0);
            _recording?.Abandon();
            _recording = null;
            _realtimeTransport?.Reset();
            _realtimeTransport = null;
            capture = _capture;
            _capture = null;
            _sink = null;
            EndpointId = null;
            ClearSignalMetrics();
            Volatile.Write(ref _status, "Stopped");
        }
        if (capture != null)
        {
            capture.DataAvailable -= OnAudio;
            capture.RecordingStopped -= OnStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
    }

    public void Dispose() => Stop();
}
