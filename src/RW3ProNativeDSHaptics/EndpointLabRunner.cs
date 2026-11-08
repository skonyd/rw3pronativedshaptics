using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace RW3ProNativeDSHaptics;

public sealed record EndpointLabReport(string Scene, string EndpointId, EndpointEmission SourceOnlyEmission,
    EndpointEmission LiveEmission, EndpointSampleComparison SourceComparison, long LiveCapturedFrames,
    long LiveLeftActiveBlocks, long LiveRightActiveBlocks, long LiveInvalidSamples, long EngineTicks, float EngineHz,
    int OutputLevelPercent, int SourceGainPercent, string? Microphone, AcousticWindow? MicrophoneActive,
    string EvidenceLimit, bool GameAuthored = false, bool PhysicalFidelityVerified = false,
    float SourceSpeakerRmsPeak = 0);

/// <summary>Real endpoint experiment: an independent process renders; only WASAPI callbacks feed Sensa.</summary>
public static class EndpointLabRunner
{
    public static async Task<EndpointLabReport> RunAsync(ChannelLabScene scene, string endpointId,
        int level, int gain, string? micId, CancellationToken token, IProgress<string>? progress = null,
        bool temporaryUnityLevel = false)
    {
        // Only the explicit lab checkbox opts into this temporary setting; ordinary Native and reference paths never use it.
        using var volumeLease = temporaryUnityLevel ? new EndpointVolumeLease(endpointId) : null;
        byte[] original = DualSenseChannelFixtures.Build(scene);
        using var capture = new NativeDualSenseCapture();
        byte[]? wav = null;
        EndpointEmission sourceEmission;
        EndpointSampleComparison comparison;
        float speakerRmsPeak;
        try
        {
            progress?.Report("1/2 • gerçek uç noktası → source-only WASAPI; en çok 8 sn RAM, dosyaya ses yazılmaz");
            capture.Start(endpointId, captureBufferMs: 10);
            var format = capture.CurrentPcmFormat;
            capture.BeginPcmRecording(8); // Explicit test action; no Sensa or transport attached.
            sourceEmission = await EmitInSeparateProcessAsync(endpointId, scene.Id, token);
            await Task.Delay(200, token);
            if (!capture.IsCapturing || capture.PcmRecordingTimedOut)
                throw new InvalidOperationException("Source-only capture stopped or exceeded its recording window.");
            wav = capture.FinishPcmRecording();
            speakerRmsPeak = capture.MaxSpeakerRms;
            comparison = EndpointSignalVerification.Compare(original, wav, format);
            if (scene.Id == "speaker" && speakerRmsPeak <= .01f)
                throw new InvalidOperationException("Speaker-only negative control did not observe front-channel energy.");
            if (!comparison.CompleteActiveRegion || comparison.SourceHasRearEnergy && comparison.Correlation < .98)
                throw new InvalidOperationException("Windows loopback source comparison failed; no live Sensa stage. Correlation=" + comparison.Correlation.ToString("F4"));
            if (!comparison.SourceHasRearEnergy && (comparison.CapturedLeftPeak > 1e-6 || comparison.CapturedRightPeak > 1e-6))
                throw new InvalidOperationException("Rear-channel negative control failed; unexpected PCM detected.");
        }
        finally
        {
            capture.Stop();
            if (wav != null) Array.Clear(wav); // No automatic captured-audio file or retained test PCM.
        }

        using var engine = new SensaStereoPrototype { OutputLevelPercent = level, SourceGainPercent = gain };
        using var stopOnCancel = token.Register(engine.Stop);
        using var microphone = micId == null ? null : new MicrophoneLevelProbe(micId);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!engine.Start()) throw new InvalidOperationException(engine.Status);
            for (int i = 0; i < 40 && !engine.IsActive && engine.IsRunning; i++) await Task.Delay(50, token);
            if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
            progress?.Report("2/2 • ayrı oynatıcı → Windows 4ch → WASAPI → kayıplı v2 → Wolverine");
            capture.Start(endpointId, engine, 10); // No direct PCM replay, recording or transport in this stage.
            microphone?.BeginWindow();
            long before = engine.TickCount;
            var liveEmission = await EmitInSeparateProcessAsync(endpointId, scene.Id, token);
            await Task.Delay(300, token);
            if (!capture.IsCapturing || !engine.IsActive || capture.CapturedFrames == 0)
                throw new InvalidOperationException("Live capture/provider stopped: " + capture.Status + " / " + engine.Status);
            if (liveEmission.OriginalSha256 != sourceEmission.OriginalSha256 ||
                liveEmission.OriginalSha256 != DualSenseChannelFixtures.Sha256(original) ||
                liveEmission.RenderSha256 != sourceEmission.RenderSha256 ||
                liveEmission.EndpointVolume != sourceEmission.EndpointVolume || liveEmission.EndpointMuted != sourceEmission.EndpointMuted)
                throw new InvalidOperationException("Source identity changed between test stages.");
            if ((comparison.CapturedLeftPeak > .03f && capture.LeftActiveBlocks == 0) ||
                (comparison.CapturedRightPeak > .03f && capture.RightActiveBlocks == 0) ||
                (comparison.CapturedLeftPeak == 0 && capture.LeftActiveBlocks != 0) ||
                (comparison.CapturedRightPeak == 0 && capture.RightActiveBlocks != 0))
                throw new InvalidOperationException("Live input-channel activity disagrees with the source-only control.");
            return new(scene.Id, endpointId, sourceEmission, liveEmission, comparison, capture.CapturedFrames,
                capture.LeftActiveBlocks, capture.RightActiveBlocks, capture.InvalidSamples,
                engine.TickCount - before, engine.MeasuredTickHz, engine.OutputLevelPercent, engine.SourceGainPercent,
                microphone?.DeviceName, microphone?.ReadWindow(),
                "Synthetic source traversed a real Windows endpoint and production WASAPI capture. Shared-mode OS processing is measured, not corrected. API activity/microphone levels are not physical fidelity or real-game provenance.",
                SourceSpeakerRmsPeak: speakerRmsPeak);
        }
        finally { capture.Stop(); engine.Stop(); }
    }

    private sealed class EndpointVolumeLease : IDisposable
    {
        private readonly MMDevice _device;
        private readonly bool _muted;
        private readonly float _volume;
        public EndpointVolumeLease(string endpointId)
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(endpointId);
            _muted = _device.AudioEndpointVolume.Mute;
            _volume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            try
            {
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = 1;
                _device.AudioEndpointVolume.Mute = false;
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try
            {
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = _volume;
                _device.AudioEndpointVolume.Mute = _muted;
            }
            finally { _device.Dispose(); }
        }
    }

    private static async Task<EndpointEmission> EmitInSeparateProcessAsync(string endpointId, string scene, CancellationToken token)
    {
        string reportPath = Path.Combine(Path.GetTempPath(), "rw3-source-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Process path missing"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        // Works from both the packaged WPF executable and `dotnet HardwareProbe.dll`.
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (string arg in new[] { "--emit-channel-fixture", endpointId, scene, reportPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic sender did not start.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(10000);
            await process.WaitForExitAsync(timeout.Token);
            string json = await File.ReadAllTextAsync(reportPath, token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Independent endpoint sender failed: " + json);
            return JsonSerializer.Deserialize<EndpointEmission>(json) ?? throw new InvalidOperationException("Sender report missing.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (File.Exists(reportPath)) File.Delete(reportPath); // Own temporary metadata only, never audio.
        }
    }
}
