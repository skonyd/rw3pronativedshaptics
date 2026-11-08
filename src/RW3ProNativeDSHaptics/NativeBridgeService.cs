using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// UI-free owner of the experimental DualSense UAC â†’ Razer Sensa bridge used by the
/// main window's "Genel" and "Test" tabs. It wraps the same lossy 33 ms-frame parametric
/// v2 backend (SensaStereoPrototype + HapticFeatureExtractor).
/// Only one HAR engine may run per process, so callers must Stop() this service
/// before starting any other test operation that owns a SensaStereoPrototype.
/// </summary>
public sealed class NativeBridgeService : IDisposable
{
    private readonly SensaStereoPrototype _sensa = new();
    private readonly NativeDualSenseCapture _source = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>v2 requests a 10 ms loopback buffer (v1: 20 ms); the driver may grant more.</summary>
    public const int BridgeCaptureBufferMs = 10;
    public float EngineTickHz => _sensa.MeasuredTickHz;

    public bool IsBridgeRunning => _source.IsCapturing && _sensa.IsActive;
    public bool IsSensaActive => _sensa.IsActive;
    public string SensaStatus => _sensa.Status;
    public bool ExperimentalMultiband
    {
        get => _sensa.ExperimentalMultiband;
        set {
            if (_sensa.IsRunning) throw new InvalidOperationException("Stop the engine before changing its output backend.");
            _sensa.ExperimentalMultiband = value;
        }
    }
    public string SourceStatus => _source.Status;
    public string? SourceName { get; private set; }
    public float LeftRms => _source.LeftRms;
    public float RightRms => _source.RightRms;
    public bool HasRecentHapticSignal =>
        HapticSafetyRules.HasRecentChannelEnergy(_source.IsCapturing,
            _source.LastLeftEnergyTick, _source.LastRightEnergyTick, Environment.TickCount64);

    /// <summary>Native v2 source gain in percent (Settings: Normal 140 / GÃ¼Ã§lÃ¼ 200 / Maksimum 300).</summary>
    public int SourceGainPercent
    {
        get => _sensa.SourceGainPercent;
        set => _sensa.SourceGainPercent = value;
    }

    public int OutputLevelPercent
    {
        get => _sensa.OutputLevelPercent;
        set => _sensa.OutputLevelPercent = value;
    }

    /// <summary>
    /// Picks the first 4ch/48k render endpoint whose name looks like a DualSense.
    /// A name match is a hint, not proof that a game authored the samples.
    /// </summary>
    public static NativeDualSenseCapture.EndpointCandidate? FindDualSenseEndpoint()
    {
        var all = NativeDualSenseCapture.EnumeratePossibleSources();
        return all.FirstOrDefault(e => e.CanCapture &&
                   (e.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)))
               ?? null;
    }

    /// <summary>Starts only the Razer engine (for side tests), without audio capture.</summary>
    public async Task EnsureSensaAsync(CancellationToken token = default)
    {
        if (_sensa.IsActive) return;
        if (!_sensa.Start()) throw new InvalidOperationException(_sensa.Status);
        for (int i = 0; i < 30 && !_sensa.IsActive && _sensa.IsRunning; i++)
            await Task.Delay(80, token);
        if (!_sensa.IsActive) throw new InvalidOperationException(_sensa.Status);
    }

    public async Task StartBridgeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var endpoint = FindDualSenseEndpoint() ?? throw new InvalidOperationException(
                "4 kanallÄ± DualSense ses aygÄ±tÄ± bulunamadÄ±. DualSense'i USB ile tak veya " +
                "sanal DualSense sÃ¼rÃ¼cÃ¼nÃ¼ (PadForge/DSX+) baÅŸlat.");
            _source.Stop();
            await EnsureSensaAsync(token);
            _source.Start(endpoint.Id, _sensa, BridgeCaptureBufferMs);
            SourceName = endpoint.Name;
            DiagnosticsService.Info("Bridge started from main window: " + endpoint.Name +
                "; lossy v2 33ms-frame parametric Sensa output; game provenance and physical HD unverified.");
        }
        catch
        {
            _source.Stop();
            _sensa.Stop();
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task<bool> TestSideAsync(HapticTestChannel side, CancellationToken token) =>
        RunTestAsync(() => _sensa.TestSideAsync(side, token), token);

    public Task<bool> TestBothAsync(CancellationToken token) =>
        RunTestAsync(() => _sensa.TestBothAsync(token), token);

    /// <summary>
    /// Feeds a synthetic DualSense rear-channel signal through the same v2 extractor and
    /// engine as live capture, in real-time 5 ms blocks. Returns the measured tick rate.
    /// </summary>
    public Task<bool> PlayDemoAsync(NativeDemo demo, CancellationToken token, IProgress<int>? elapsedMs = null) =>
        RunTestAsync(async () =>
        {
            var pcm = DualSenseChannelFixtures.Build(new ChannelLabScene("demo:" + demo, NativeDemoSignals.Title(demo), demo));
            _sensa.Features.Reset();
            await ChannelLabRunner.ReplaySourceAsync(pcm, _sensa, token, elapsedMs);
            return _sensa.IsActive;
        }, token);


    private async Task<bool> RunTestAsync(Func<Task<bool>> test, CancellationToken token)
    {
        // Live game PCM would mask a test pulse, so tests pause audio capture.
        _source.Stop();
        await EnsureSensaAsync(token);
        return await test();
    }

    /// <summary>
    /// Restarts the Razer engine so changed L/R targets take effect
    /// (targets are bound when the engine creates its two events).
    /// </summary>
    public void RestartEngine()
    {
        _source.Stop();
        _sensa.Stop();
    }

    private AudioHapticsBridge? _audio;
    /// <summary>PC-mode audio → Sensa path is running.</summary>
    public bool IsAudioToSensaRunning => _audio?.IsCapturing == true && _sensa.IsActive;
    public bool AudioHasRecentSound => _audio?.HasRecentSound == true;
    public string? AudioDeviceName => _audio?.DeviceName;
    public int AudioSyncDelayMs => _audio?.EffectiveDelayMs ?? 0;
    public string? AudioFailure => _audio?.Failure;

    /// <summary>Game audio (default playback device) → haptic low-pass → v2 Sensa engine.</summary>
    public async Task StartAudioToSensaAsync(CancellationToken token = default, string? deviceId = null, int? syncDelayOverrideMs = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            _source.Stop();
            _audio?.Dispose();
            _audio = null;
            await EnsureSensaAsync(token);
            _sensa.Features.Reset();
            _audio = new AudioHapticsBridge(new SensaAudioSink(_sensa.Features)) { DelayOverrideMs = syncDelayOverrideMs };
            _audio.Start(deviceId);
        }
        catch
        {
            _audio?.Dispose(); _audio = null;
            _sensa.Stop();
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Stop()
    {
        _audio?.Dispose();
        _audio = null;
        _source.Stop();
        _sensa.Stop();
        SourceName = null;
    }

    public void Dispose()
    {
        Stop();
        _source.Dispose();
        _sensa.Dispose();
        _gate.Dispose();
    }
}
