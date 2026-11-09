using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

public sealed record AcousticAuditScene(string Name, double FrequencyHz, double Amplitude, bool Right, int DriveMs, string Pattern = "tone");
public sealed record SoftwareFrameObservation(int NonIdleFrames, double LeftFeaturePeak, double RightFeaturePeak,
    double LeftEnvelopeAfterGain, double RightEnvelopeAfterGain, int SaturatedEnvelopePoints);
public sealed record AcousticAuditReport(AcousticAuditScene Scene, string Route, int Repeat,
    string OriginalPcmSha256, AcousticWindow Background, AcousticWindow EngineIdle, AcousticWindow DrivenAndTail,
    AcousticWindow EngineOffAfter, double RequestedSourceDurationMs, double CommandElapsedMs,
    long RenderTicks, float EngineHz, long AudioCallbacks, long AudioInputSamples, long ClippedAudioSamples,
    float VolumeCompensation, int SourceGainPercent, string Microphone, string? AudioFailure,
    string Limit, bool PhysicalFidelityVerified = false, bool SourceCancelled = false, SoftwareFrameObservation? SoftwareFrames = null,
    bool EngineKeptAlive = false, bool MicrophoneKeptOpen = false, float AudioInputGain = SensaAudioSink.PreGain,
    int RequestedEngineTickMs = 33, bool ExperimentalMultiband = false);

/// <summary>Repeatable relative acoustic observations, not calibrated actuator measurements.</summary>
public static class SingleMicrophoneAudit
{
    public static IEnumerable<AcousticAuditScene> SweepScenes()
    {
        foreach (double hz in new[] { 30d, 40, 50, 60, 80, 100, 120, 150, 190, 250, 300, 350, 400 })
            foreach (double level in new[] { .2, .5, .9 })
                yield return new($"tone-{hz}-{level}", hz, level, false, 1000);
        yield return new("right-80", 80, .5, true, 1000);
        yield return new("right-190", 190, .5, true, 1000);
    }

    public static byte[] BuildTone(AcousticAuditScene scene)
    {
        if (!double.IsFinite(scene.FrequencyHz) || !double.IsFinite(scene.Amplitude) ||
            scene.FrequencyHz is < 20 or > 400 || scene.Amplitude is < 0 or > .9 || scene.DriveMs is < 100 or > 7000 ||
            scene.Pattern is not ("tone" or "stereo" or "bursts" or "alternating" or "mixed"))
            throw new ArgumentOutOfRangeException(nameof(scene));
        int frames = (scene.DriveMs + 500) * 48;
        byte[] pcm = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            double t = i / 48000.0 - .2;
            if (t < 0 || t >= scene.DriveMs / 1000.0) continue;
            double fade = Math.Clamp(Math.Min(t, scene.DriveMs / 1000.0 - t) / .01, 0, 1);
            if (scene.Pattern == "bursts" && t % .1 >= .02) continue;
            double wave = scene.Pattern == "mixed"
                ? .5 * (Math.Sin(2 * Math.PI * scene.FrequencyHz * t) + Math.Sin(2 * Math.PI * 190 * t))
                : Math.Sin(2 * Math.PI * scene.FrequencyHz * t);
            short value = (short)Math.Round(wave * scene.Amplitude * fade * short.MaxValue);
            bool right = scene.Pattern == "alternating" ? (int)(t / .1) % 2 != 0 : scene.Right;
            int offset = i * 8 + (right ? 6 : 4);
            pcm[offset] = (byte)value; pcm[offset + 1] = (byte)(value >> 8);
            if (scene.Pattern == "stereo")
            {
                short r = (short)Math.Round(Math.Sin(2 * Math.PI * 190 * t) * scene.Amplitude * fade * short.MaxValue);
                pcm[i * 8 + 6] = (byte)r; pcm[i * 8 + 7] = (byte)(r >> 8);
            }
        }
        return pcm;
    }

    public static async Task<AcousticAuditReport> RunAsync(AcousticAuditScene scene, string route, int repeat,
        string micId, string playbackId, CancellationToken token, bool useWaveIn = false,
        SensaStereoPrototype? sharedEngine = null, MicrophoneLevelProbe? sharedMicrophone = null, bool experimentalMultiband = false)
    {
        bool cancelSource = route.StartsWith("cancel-", StringComparison.Ordinal);
        string activeRoute = cancelSource ? route[7..] : route;
        if (activeRoute is not ("reference" or "audio" or "off-audio" or "idle") ||
            cancelSource && activeRoute is not ("reference" or "audio")) throw new ArgumentException("Unknown route");
        byte[] pcm = BuildTone(scene);
        string hash = DualSenseChannelFixtures.Sha256(pcm);
        Console.WriteLine("  microphone opening");
        using var ownedMicrophone = sharedMicrophone == null ? new MicrophoneLevelProbe(micId, useWaveIn) : null;
        var microphone = sharedMicrophone ?? ownedMicrophone!;
        Console.WriteLine("  microphone started");
        await Task.Delay(800, token);
        microphone.BeginWindow(scene.FrequencyHz);
        await Task.Delay(800, token);
        var background = microphone.ReadWindow();
        Console.WriteLine("  background samples=" + background.Samples);
        using var ownedEngine = sharedEngine == null ? new SensaStereoPrototype { OutputLevelPercent = 100, SourceGainPercent = 140, ExperimentalMultiband = experimentalMultiband } : null;
        var engine = sharedEngine ?? ownedEngine!;
        var frameGate = new object();
        int nonIdleFrames = 0, saturatedPoints = 0;
        double leftPeak = 0, rightPeak = 0, leftCommand = 0, rightCommand = 0;
        engine.FrameProbe = (left, right, level, idleFrame) =>
        {
            lock (frameGate)
            {
            if (idleFrame) return;
            nonIdleFrames++;
            leftPeak = Math.Max(leftPeak, left.Peak); rightPeak = Math.Max(rightPeak, right.Peak);
            leftCommand = Math.Max(leftCommand, Math.Clamp(left.Peak * level, 0, 1));
            rightCommand = Math.Max(rightCommand, Math.Clamp(right.Peak * level, 0, 1));
            saturatedPoints += (left.P0 * level > 1 ? 1 : 0) + (left.P1 * level > 1 ? 1 : 0) +
                (left.P2 * level > 1 ? 1 : 0) + (left.P3 * level > 1 ? 1 : 0) +
                (right.P0 * level > 1 ? 1 : 0) + (right.P1 * level > 1 ? 1 : 0) +
                (right.P2 * level > 1 ? 1 : 0) + (right.P3 * level > 1 ? 1 : 0);
            }
        }; // Existing observation hook only; never changes the provider render path.
        using var stopOnCancel = token.Register(engine.Stop);
        AudioHapticsBridge? audio = null;
        var sink = new SensaAudioSink(engine.Features);
        try
        {
            if (activeRoute != "off-audio")
            {
                Console.WriteLine("  engine starting");
                if (!engine.IsActive && !engine.Start()) throw new InvalidOperationException(engine.Status);
                for (int i = 0; i < 40 && !engine.IsActive && engine.IsRunning; i++) await Task.Delay(50, token);
                if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
                Console.WriteLine("  engine active");
            }
            microphone.BeginWindow(scene.FrequencyHz);
            await Task.Delay(500, token);
            var idle = microphone.ReadWindow();
            if (activeRoute == "audio")
            {
                audio = new AudioHapticsBridge(sink) { DelayOverrideMs = 0 };
                audio.Start(playbackId);
            }
            microphone.BeginWindow(scene.FrequencyHz);
            long ticks = engine.EngineTicks;
            var clock = Stopwatch.StartNew();
            bool sourceCancelled = false;
            using var sourceStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (cancelSource) sourceStop.CancelAfter(700);
            try
            {
                if (activeRoute == "reference") await ChannelLabRunner.ReplaySourceAsync(pcm, engine, sourceStop.Token);
                else if (activeRoute is "audio" or "off-audio") await PlayStereoAsync(pcm, playbackId, sourceStop.Token);
                else await Task.Delay(pcm.Length / 8 / 48, token);
            }
            catch (OperationCanceledException) when (cancelSource && sourceStop.IsCancellationRequested && !token.IsCancellationRequested)
            { sourceCancelled = true; }
            if (cancelSource && !sourceCancelled) throw new InvalidOperationException("Expected source cancellation did not occur.");
            double commandMs = clock.Elapsed.TotalMilliseconds;
            await Task.Delay(1800, token); // Leave engine/capture running to expose a growing tail.
            var activeAndTail = microphone.ReadWindow();
            Console.WriteLine("  measured; clearing source");
            long renders = engine.EngineTicks - ticks;
            if (activeRoute != "off-audio" && (renders <= 0 || !engine.IsActive))
                throw new InvalidOperationException("Sensa provider did not remain active: " + engine.Status);
            float rate = engine.MeasuredTickHz;
            long callbacks = audio?.Callbacks ?? 0;
            float comp = audio?.VolumeCompensation ?? 1;
            string? failure = audio?.Failure;
            if (audio != null && (!audio.IsCapturing || callbacks == 0 || sink.InputSamples == 0 || !audio.HasRecentSound))
                throw new InvalidOperationException("No valid real audio capture: " + failure);
            audio?.Dispose(); audio = null;
            if (sharedEngine == null) engine.Stop(); else engine.Features.ClearForSilence();
            Console.WriteLine(sharedEngine == null ? "  engine stopped" : "  engine kept running at idle");
            microphone.BeginWindow(scene.FrequencyHz);
            await Task.Delay(800, token);
            var off = microphone.ReadWindow();
            if (microphone.Failure != null || new[] { background, idle, activeAndTail, off }.Any(w => w.Samples == 0 || w.TruncatedBins > 0))
                throw new InvalidOperationException("Microphone samples missing/truncated: " + microphone.Failure);
            if (hash != DualSenseChannelFixtures.Sha256(pcm)) throw new InvalidOperationException("Original source mutated.");
            SoftwareFrameObservation frames;
            lock (frameGate) frames = new(nonIdleFrames, leftPeak, rightPeak, leftCommand, rightCommand, saturatedPoints);
            return new(scene, route, repeat, hash, background, idle, activeAndTail, off, pcm.Length / 8 / 48.0,
                commandMs, renders, rate, callbacks, sink.InputSamples, sink.ClippedSamples, comp,
                engine.SourceGainPercent, microphone.DeviceName, failure,
                (useWaveIn ? "Explicit Windows MME/PCM16 microphone capture. " : "WASAPI Float32 microphone capture. ") +
                "Single uncalibrated airborne/desk mic, first channel. 10ms bins use microphone sample time, not calibrated command/actuator latency. " +
                "Reference means synthetic Native-extractor-to-Wolverine, NOT physical DualSense. AudioInputGain records the test gain; at unity the source is not pre-attenuated. Output gain can still saturate parametric envelopes. " +
                "Room/AGC/noise-gate and mechanical coupling remain confounders. No raw microphone audio stored. " +
                (sharedEngine != null ? "EngineKeptAlive: EngineOffAfter is post-source-cleanup IDLE, not engine off. " : ""),
                SourceCancelled: sourceCancelled,
                SoftwareFrames: frames, EngineKeptAlive: sharedEngine != null, MicrophoneKeptOpen: sharedMicrophone != null,
                AudioInputGain: sink.InputGain, RequestedEngineTickMs: SensaStereoPrototype.TickMs,
                ExperimentalMultiband: engine.ExperimentalMultiband);
        }
        finally
        {
            audio?.Dispose();
            if (sharedEngine == null) engine.Stop(); else engine.Features.ClearForSilence();
            engine.FrameProbe = null;
        }
    }

    private static async Task PlayStereoAsync(byte[] quad, string endpointId, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        int rate = device.AudioClient.MixFormat.SampleRate;
        if (rate != 48000) throw new NotSupportedException("Acoustic audit requires an explicit 48k test endpoint; no silent resampling of the reference.");
        byte[] stereoFloat = new byte[quad.Length];
        for (int frame = 0; frame < quad.Length / 8; frame++)
        {
            BitConverter.TryWriteBytes(stereoFloat.AsSpan(frame * 8, 4), BitConverter.ToInt16(quad, frame * 8 + 4) / 32768f / SensaAudioSink.PreGain);
            BitConverter.TryWriteBytes(stereoFloat.AsSpan(frame * 8 + 4, 4), BitConverter.ToInt16(quad, frame * 8 + 6) / 32768f / SensaAudioSink.PreGain);
        }
        using var memory = new MemoryStream(stereoFloat, writable: false);
        using var source = new RawSourceWaveStream(memory, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        output.Init(source);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception != null) done.TrySetException(e.Exception); else done.TrySetResult(); };
        try { output.Play(); await done.Task.WaitAsync(TimeSpan.FromMilliseconds(quad.Length / 8 / 48 + 2000), token); }
        finally { output.Stop(); }
    }
}
