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
    private SensaStereoPrototype? _sink;
    private long _frames, _lastFrameTick, _leftEnergyFrames, _rightEnergyFrames;
    private int _capturing;
    private string _status = "Not started";
    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;
    public long CapturedFrames => Interlocked.Read(ref _frames);
    public long LastFrameTick => Interlocked.Read(ref _lastFrameTick);
    public long LeftActiveBlocks => Interlocked.Read(ref _leftEnergyFrames);
    public long RightActiveBlocks => Interlocked.Read(ref _rightEnergyFrames);
    public string Status => Volatile.Read(ref _status);
    public string? EndpointId { get; private set; }

    public static IReadOnlyList<(string Id, string Name, int Channels)> EnumeratePossibleSources()
    {
        var results = new List<(string, string, int)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            // Show all endpoints so users can diagnose. Starting capture rejects channels < 4.
            try
            {
                int channels = device.AudioClient.MixFormat.Channels;
                results.Add((device.ID, device.FriendlyName, channels));
            }
            catch { /* Unavailable or stale endpoint: do not crash enumeration. */ }
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
        bool float32 = fmt.BitsPerSample == 32 &&
            (fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
            fmt is WaveFormatExtensible ef &&
            ef.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        bool pcm16 = fmt.BitsPerSample == 16 &&
            (fmt.Encoding == WaveFormatEncoding.Pcm ||
            fmt is WaveFormatExtensible ep &&
            ep.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));
        // Prevent accidentally mistaking ordinary 2-channel game audio for DualSense haptic PCM.
        if (fmt.Channels != 4 || (!float32 && !pcm16) || fmt.SampleRate != 48000)
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
        EndpointId = endpointId;
        Interlocked.Exchange(ref _frames, 0);
        Interlocked.Exchange(ref _lastFrameTick, 0);
        Interlocked.Exchange(ref _leftEnergyFrames, 0);
        Interlocked.Exchange(ref _rightEnergyFrames, 0);
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
        var fmt = capture.WaveFormat;
        int bytes = fmt.BitsPerSample / 8;
        int frameBytes = fmt.Channels * bytes;
        int count = args.BytesRecorded / frameBytes;
        if (count == 0) return;
        Interlocked.Add(ref _frames, count);
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);

        // Only channels 2 & 3 (zero-indexed) carry haptic PCM. Channel 0/1 is speaker.
        // <=256 sample points/callback, no memory allocations proportional to PCM length.
        int stride = Math.Max(1, (count + 255) / 256);
        double leftEnergy = 0, rightEnergy = 0;
        int n = 0;
        for (int i = 0; i < count; i += stride)
        {
            int offset = i * frameBytes + 2 * bytes;
            float l = bytes == 4
                ? BitConverter.ToSingle(args.Buffer, offset)
                : BitConverter.ToInt16(args.Buffer, offset) / 32768f;
            float r = bytes == 4
                ? BitConverter.ToSingle(args.Buffer, offset + bytes)
                : BitConverter.ToInt16(args.Buffer, offset + bytes) / 32768f;
            if (!float.IsFinite(l) || !float.IsFinite(r)) continue;
            leftEnergy += Math.Clamp(l, -1, 1) * Math.Clamp(l, -1, 1);
            rightEnergy += Math.Clamp(r, -1, 1) * Math.Clamp(r, -1, 1);
            n++;
        }
        if (n == 0) return;
        float la = (float)Math.Sqrt(leftEnergy / n);
        float ra = (float)Math.Sqrt(rightEnergy / n);
        if (la > 0.01f) Interlocked.Increment(ref _leftEnergyFrames);
        if (ra > 0.01f) Interlocked.Increment(ref _rightEnergyFrames);

        // Lossy conversion: 48k stereo PCM → ~50Hz parametric envelope.
        // Not bit-perfect transfer or guaranteed side-specific physical response.
        _sink?.Submit(Math.Min(0.33f, la * 2.5f), Math.Min(0.33f, ra * 2.5f));
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        Volatile.Write(ref _capturing, 0);
        _sink?.Submit(0, 0);
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
