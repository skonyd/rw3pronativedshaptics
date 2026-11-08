using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace RW3ProNativeDSHaptics;

public sealed record LabMicrophone(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ChannelLabReport(string Scene, string SourceSha256, string SourceAfterSha256,
    int SourceFrames, string Route, int OutputLevelPercent, int SourceGainPercent,
    long RenderTicks, float EngineHz, double MaxReplayLatenessMs,
    string? Microphone, AcousticWindow? Background, AcousticWindow? Active, AcousticWindow? Tail,
    string? MicrophoneFailure, string EvidenceLimit,
    bool GameAuthored = false, bool PhysicalFidelityVerified = false,
    ChannelSourceAnalysis? SoftwareSourceAnalysis = null);

/// <summary>Explicit synthetic test only. No fixtures are passed off as game captures.</summary>
public static class ChannelLabRunner
{
    public static IReadOnlyList<LabMicrophone> Microphones()
    {
        var result = new List<LabMicrophone>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        return result;
    }

    public static async Task<ChannelLabReport> ReplayAsync(ChannelLabScene scene, int level, int gain,
        string? microphoneId, CancellationToken token, IProgress<string>? progress = null)
    {
        byte[] pcm = DualSenseChannelFixtures.Build(scene);
        string hash = DualSenseChannelFixtures.Sha256(pcm);
        using var engine = new SensaStereoPrototype { OutputLevelPercent = level, SourceGainPercent = gain };
        level = engine.OutputLevelPercent;
        gain = engine.SourceGainPercent;
        using var stopOnCancel = token.Register(engine.Stop);
        using var microphone = microphoneId == null ? null : new MicrophoneLevelProbe(microphoneId);
        AcousticWindow? background = null, active = null, tail = null;
        if (microphone != null)
        {
            // Exclude WASAPI/microphone startup transients from the baseline.
            await Task.Delay(750, token);
            progress?.Report("Mikrofon arka planı • 1 saniye");
            microphone.BeginWindow();
            await Task.Delay(1000, token);
            background = microphone.ReadWindow();
            if (background.Samples == 0 || microphone.Failure != null)
                throw new InvalidOperationException("Mikrofon veri sağlamıyor: " + microphone.Failure);
        }
        if (!engine.Start()) throw new InvalidOperationException(engine.Status);
        try
        {
            for (int i = 0; i < 40 && !engine.IsActive && engine.IsRunning; i++) await Task.Delay(50, token);
            if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
            engine.Features.Reset();
            progress?.Report("Özgün 4ch PCM → ortak giriş → kayıplı v2 → Wolverine");
            microphone?.BeginWindow();
            long firstTick = engine.TickCount;
            double lateness = await Task.Run(() => Replay(pcm, engine, token), token);
            await Task.Delay(200, token); // exercise source timeout while engine is still active
            active = microphone?.ReadWindow();
            long rendered = engine.TickCount - firstTick;
            float hz = engine.MeasuredTickHz;
            if (!engine.IsActive || rendered <= 0) throw new InvalidOperationException("Sensa işçisi durdu: " + engine.Status);
            engine.Stop();
            if (microphone != null)
            {
                progress?.Report("Motor durdu • mikrofon son kontrolü");
                await Task.Delay(200, token);
                microphone.BeginWindow();
                await Task.Delay(1000, token);
                tail = microphone.ReadWindow();
                if (tail.Samples == 0 || microphone.Failure != null)
                    throw new InvalidOperationException("Mikrofon ölçümü tamamlanmadı: " + microphone.Failure);
            }
            return new(scene.Id, hash, DualSenseChannelFixtures.Sha256(pcm), pcm.Length / 8,
                "Synthetic PCM replay at shared decoder boundary; NO USB/WASAPI source capture", level, gain,
                rendered, hz, lateness, microphone?.DeviceName, background, active, tail, microphone?.Failure,
                "Microphone levels are acoustic observations, not hand feel, side isolation, waveform equivalence or calibrated latency. Reference DualSense not played.",
                SoftwareSourceAnalysis: DualSenseChannelFixtures.Analyze(pcm, level, gain));
        }
        finally { engine.Stop(); }
    }

    public static Task<double> ReplaySourceAsync(byte[] pcm, SensaStereoPrototype engine,
        CancellationToken token, IProgress<int>? progress = null) => Task.Run(() => Replay(pcm, engine, token, progress), token);

    private static double Replay(byte[] pcm, SensaStereoPrototype engine, CancellationToken token, IProgress<int>? progress = null)
    {
        // Source schedule has its own high-resolution timer. Never silently fall back to Sleep ticks.
        using var timer = new ReplayTimer();
        var clock = Stopwatch.StartNew();
        double maxLate = 0;
        for (int offset = 0; offset < pcm.Length; offset += 240 * 8)
        {
            token.ThrowIfCancellationRequested();
            if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
            double targetMs = offset / 8 / 48.0;
            double remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining > 0) timer.Wait(remaining);
            token.ThrowIfCancellationRequested();
            maxLate = Math.Max(maxLate, clock.Elapsed.TotalMilliseconds - targetMs);
            NativePcmIngress.Push(pcm.AsSpan(offset, Math.Min(240 * 8, pcm.Length - offset)),
                FourChannelHapticAnalyzer.PcmEncoding.Pcm16, engine.Features);
            if (offset % (240 * 8 * 10) == 0) progress?.Report((int)targetMs);
        }
        return maxLate;
    }

    public static string SaveReport(ChannelLabReport report)
    {
        string folder = Path.Combine(Path.GetDirectoryName(DiagnosticsService.LogFile)!, "channel-lab");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private sealed class ReplayTimer : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private readonly IntPtr _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003);
        public ReplayTimer()
        {
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("High-resolution PCM replay timer unavailable.");
        }
        public void Wait(double ms)
        {
            long due = -Math.Max(1, (long)(ms * 10000));
            if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) || WaitForSingleObject(_handle, 250) != 0)
                throw new InvalidOperationException("PCM replay timer failed.");
        }
        public void Dispose() => CloseHandle(_handle);
    }
}
