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
    private WasapiLoopbackCapture? _capture;
    private FourChannelHapticAnalyzer.PcmEncoding _encoding;
    private SensaStereoPrototype? _sink;
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

    public void Start(string endpointId, SensaStereoPrototype sink)
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
        if (!sink.IsActive)
        {
            capture.Dispose();
            throw new InvalidOperationException("Sensa output provider is not active; no silent XInput fallback.");
        }
        _sink = sink;
        _encoding = encoding.Value;
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
        _capture = capture;
        capture.DataAvailable += OnAudio;
        capture.RecordingStopped += OnStopped;
        Volatile.Write(ref _capturing, 1);
        try
        {
            capture.StartRecording();
            Volatile.Write(ref _status,
                "Four-channel UAC loopback ACTIVE; stereo authored haptic channel identity still needs game confirmation");
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

        // Lossy conversion: 48k stereo PCM → ~50Hz parametric envelope.
        // Not bit-perfect transfer or guaranteed side-specific physical response.
        _sink?.Submit(Math.Min(0.33f, la * 2.5f), Math.Min(0.33f, ra * 2.5f));
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        Volatile.Write(ref _capturing, 0);
        _sink?.Submit(0, 0);
        Volatile.Write(ref _leftRmsBits, 0);
        Volatile.Write(ref _rightRmsBits, 0);
        Volatile.Write(ref _speakerRmsBits, 0);
        Volatile.Write(ref _status,
            args.Exception is null ? "Audio capture unexpectedly stopped" :
            "Audio capture stopped: " + args.Exception.Message);
    }

    public void Stop()
    {
        Volatile.Write(ref _capturing, 0);
        _sink?.Submit(0, 0);
        var capture = _capture;
        _capture = null;
        _sink = null;
        EndpointId = null;
        Volatile.Write(ref _leftRmsBits, 0);
        Volatile.Write(ref _rightRmsBits, 0);
        Volatile.Write(ref _speakerRmsBits, 0);
        Volatile.Write(ref _leftPeakBits, 0);
        Volatile.Write(ref _rightPeakBits, 0);
        if (capture != null)
        {
            capture.DataAvailable -= OnAudio;
            capture.RecordingStopped -= OnStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        Volatile.Write(ref _status, "Stopped");
    }

    public void Dispose() => Stop();
}
