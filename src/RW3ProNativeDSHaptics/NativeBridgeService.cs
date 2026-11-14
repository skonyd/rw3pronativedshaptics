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
    /// <summary>The user's choice. The engine may run Direct USB instead (see <see cref="EffectiveTransport"/>).</summary>
    public SensaTransport Transport
    {
        get => _preferredTransport;
        set {
            if (_sensa.IsRunning) throw new InvalidOperationException("Stop the engine before changing its transport.");
            _preferredTransport = value;
            _sensa.Transport = value;
        }
    }
    private SensaTransport _preferredTransport = SensaTransport.DirectUsb;
    /// <summary>What the engine actually uses.</summary>
    public SensaTransport EffectiveTransport => _sensa.Transport;
    /// <summary>Why the engine is not on the chosen transport (null when it is).</summary>
    public string? TransportFallbackReason { get; private set; }

    /// <summary>Synapse chosen but not usable (closed, or the controller not attached): run Direct USB,
    /// which needs nothing else. Without this the engine "ran" on the Synapse path and nothing reached
    /// the palms while Synapse was closed (virtual DualSense test, 2026-11-08).</summary>
    private void ResolveTransport()
    {
        var t = _preferredTransport;
        string? reason = null;
        if (t == SensaTransport.Synapse)
        {
            // Sensa exists only in PC mode; the Razer library throws (SEH) at initialization otherwise
            // (Xbox mode with Synapse open, 2026-11-08).
            var mode = WolverineModeControl.Detect();
            if (mode != WolverineControllerMode.Pc) { t = SensaTransport.DirectUsb; reason = "kol " + (mode == WolverineControllerMode.Xbox ? "Xbox modunda" : "bulunamadı"); }
            else if (!SynapseSensaCheck.UsableNow(out var why)) { t = SensaTransport.DirectUsb; reason = why; }
        }
        if (reason != TransportFallbackReason)
            DiagnosticsService.Info(reason != null ? "Synapse path not usable (" + reason + "): engine uses Direct USB." : "Engine transport: " + t + ".");
        TransportFallbackReason = reason;
        _sensa.Transport = t;
    }
    public long DirectFramesSent => _sensa.DirectFramesSent;
    public SensaOutputMethod OutputMethod
    {
        get => _sensa.OutputMethod;
        set {
            if (_sensa.IsRunning) throw new InvalidOperationException("Stop the engine before changing its output backend.");
            _sensa.OutputMethod = value;
        }
    }
    public (long Multi, long Standard) HybridFrames => (_sensa.HybridMultiFrames, _sensa.HybridStandardFrames);
    public string SourceStatus => _source.Status;
    public string? SourceName { get; private set; }
    public float LeftRms => _source.LeftRms;
    public float RightRms => _source.RightRms;
    public bool HasRecentHapticSignal =>
        HapticSafetyRules.HasRecentChannelEnergy(_source.IsCapturing,
            _source.LastLeftEnergyTick, _source.LastRightEnergyTick, Environment.TickCount64);

    private int _nativeGainPercent = 140, _audioSensitivityPercent = 100;
    private bool _sensitivityActive;

    /// <summary>Native v2 source gain in percent (Settings: Normal 140 / Güçlü 200 / Maksimum 300).</summary>
    public int SourceGainPercent
    {
        get => _nativeGainPercent;
        set { _nativeGainPercent = value; ApplyGain(); }
    }

    /// <summary>
    /// "Sesten titreşim" sensitivity: extra feature-level gain for game audio only (game mixes are
    /// quieter than DualSense haptic channels and vary per title). Applied after extraction, so the
    /// waveform is never clipped; <see cref="HapticLimiter"/> keeps strong moments distinct.
    /// Not applied to the Test tab sound tests, which must reach the engine at the demo level.
    /// </summary>
    public int AudioSensitivityPercent
    {
        get => _audioSensitivityPercent;
        set { _audioSensitivityPercent = Math.Clamp(value, 10, 300); XboxAudioSink.LevelPercent = _audioSensitivityPercent; ApplyGain(); }
    }

    private void ApplyGain() =>
        _sensa.SourceGainPercent = _sensitivityActive ? _nativeGainPercent * _audioSensitivityPercent / 100 : _nativeGainPercent;

    public int OutputLevelPercent
    {
        get => _sensa.OutputLevelPercent;
        set { _sensa.OutputLevelPercent = value; if (_virtual != null) _virtual.OutputLevelPercent = value; }
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
        if (!_sensa.IsRunning) ResolveTransport();
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
            _sensitivityActive = false;
            ApplyGain();
            var endpoint = FindDualSenseEndpoint() ?? throw new InvalidOperationException(
                "4 kanallı DualSense ses aygıtı bulunamadı. Ayarlar → Sanal DualSense → köprüyü başlat " +
                "veya DualSense'i USB ile tak.");
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
    public async Task StartAudioToSensaAsync(CancellationToken token = default, string? deviceId = null, int? syncDelayOverrideMs = null,
        bool applySensitivity = true, AudioHapticProfile profile = AudioHapticProfile.Existing)
    {
        await _gate.WaitAsync(token);
        try
        {
            _sensitivityActive = applySensitivity;
            ApplyGain();
            _source.Stop();
            _audio?.Dispose();
            _audio = null;
            await EnsureSensaAsync(token);
            _sensa.Features.Reset();
            _audio = new AudioHapticsBridge(new SensaAudioSink(_sensa.Features)) { DelayOverrideMs = syncDelayOverrideMs, Profile = profile, ApplyLevelShaping = applySensitivity };
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

    private VirtualDualSenseBridge? _virtual;
    public bool IsVirtualDualSenseRunning => _virtual?.IsRunning == true;
    public string VirtualDualSenseStatus => _virtual?.Status ?? "Durdu";
    public string VirtualDualSenseInputCheck => _virtual?.InputCheck ?? "";
    public string VirtualDualSenseHidHideNote => _virtual?.HidHideNote ?? "";
    public bool HidePadInGames { get; set; } = true;
    private bool _triggerMotors = true;
    private bool _palmTriggerFeel;
    /// <summary>PC mode: DualSense trigger effects felt in the palms (Sensa) instead of body rumble. Applies live.</summary>
    public bool PalmTriggerFeel
    {
        get => _palmTriggerFeel;
        set { _palmTriggerFeel = value; if (_virtual != null) _virtual.PalmTriggerFeel = value; }
    }
    private XboxNativeMix _xboxMix = new();
    public XboxNativeMix XboxMix
    {
        get => _xboxMix;
        set { _xboxMix = value; if (_virtual != null) _virtual.XboxMix = value; }
    }
    /// <summary>Xbox mode: trigger effects on the trigger motors (true) or classic body rumble. Applies live.</summary>
    public bool TriggerMotors
    {
        get => _triggerMotors;
        set { _triggerMotors = value; if (_virtual != null) _virtual.TriggerMotors = value; }
    }
    public string? VirtualDualSenseHidPath => _virtual?.VirtualHidPath;
    public WolverineControllerMode VirtualDualSenseMode => _virtual?.ControllerMode ?? WolverineControllerMode.NotFound;
    public long VirtualXboxMotorWrites => _virtual?.XboxMotorWrites ?? 0;
    public string VirtualXboxStatus => _virtual?.XboxStatus ?? "";
    public (float Left, float Right) VirtualHapticPeaks => _virtual?.PeaksSinceMark ?? (0, 0);
    public void MarkVirtualHapticPeaks() => _virtual?.MarkPeaks();
    public string SensaEngineSummary =>
        $"Sensa {(_sensa.IsRunning ? "running" : "STOPPED")}/{(_sensa.IsActive ? "active" : "inactive")}, frames {_sensa.DirectFramesSent}, " +
        $"ticks {_sensa.EngineTicks}, slots {_sensa.Features.CompletedSlots}, gain {SourceGainPercent}%, level {_sensa.OutputLevelPercent}%, '{_sensa.Status}'" +
        (XboxSensa.Active ? $", Sensa trigger raw max {XboxSensa.TakeMaxSent()}" : "");
    public (long Haptic, long Output, long Trigger) VirtualDualSenseCounters =>
        _virtual == null ? (0, 0, 0) : (_virtual.HapticBlocks, _virtual.OutputReports, _virtual.TriggerReports);

    /// <summary>
    /// Virtual DualSense mode: the game's haptic PCM arrives from the HIDMaestro device straight into
    /// the v2 engine (no loopback); trigger effects go to the Wolverine's rumble motors. Admin only.
    /// </summary>
    public async Task<bool> StartVirtualDualSenseAsync(int xinputSlot, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _source.Stop();
            _audio?.Dispose(); _audio = null;
            _sensitivityActive = false; ApplyGain();
            // The bridge does not depend on Sensa: in Xbox mode there is none and triggers/body motors go
            // through GameInput. A Sensa start failure is logged; the watchdog keeps retrying.
            try { await EnsureSensaAsync(token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticsService.Info("Virtual DualSense: Sensa engine not available (" + ex.Message + "); bridge continues.");
                try { _sensa.Stop(); } catch { }
            }
            _sensa.Features.Reset();
            _virtual ??= new VirtualDualSenseBridge();
            _virtual.HidePhysicalPad = HidePadInGames;
            _virtual.TriggerMotors = _triggerMotors;
            _virtual.PalmTriggerFeel = _palmTriggerFeel;
            _virtual.XboxMix = _xboxMix;
            _virtual.AudioHaptics = _nativeAudioHaptics;
            _virtual.OutputLevelPercent = OutputLevelPercent;
            _virtual.EngineProbe = () => SensaEngineSummary;
            // The bridge feeds the engine's feature extractor; if the engine thread is gone, nothing
            // reaches the palms although the game's haptic audio keeps arriving. Restart it.
            _virtual.EngineWatchdog = () =>
            {
                if (_sensa.IsRunning) return null;
                ResolveTransport();
                bool ok = _sensa.Start();
                return "Sensa engine was stopped; restart " + (ok ? "ok" : "failed: " + _sensa.Status);
            };
            bool started = _virtual.Start(_sensa.Features, xinputSlot);
            if (started && _nativeAudioHaptics) StartNativeAudio();
            return started;
        }
        finally { _gate.Release(); }
    }

    public void StopVirtualDualSense() { StopNativeAudio(); _virtual?.Stop(); }

    // ── Native mode, haptic source "Oyun sesinden": PC sound → the same Sensa extractor, while the virtual
    //    DualSense keeps doing trigger effects and rumble. The game's haptic PCM is then not pushed (one producer).
    private AudioHapticsBridge? _nativeAudio;
    private bool _nativeAudioHaptics;

    /// <summary>Native mode palm haptics from the PC's sound instead of the game's HD PCM. Applies live.</summary>
    public bool NativeAudioHaptics
    {
        get => _nativeAudioHaptics;
        set
        {
            _nativeAudioHaptics = value;
            if (_virtual != null) _virtual.AudioHaptics = value;
            if (!value) StopNativeAudio();
            else if (IsVirtualDualSenseRunning) StartNativeAudio();
        }
    }

    public bool IsNativeAudioRunning => _nativeAudio?.IsCapturing == true;
    public bool NativeAudioHasRecentSound => _nativeAudio?.HasRecentSound == true;
    public string? NativeAudioFailure => _nativeAudio?.Failure;
    public string? NativeAudioDevice => _nativeAudio?.DeviceName;

    private void StartNativeAudio()
    {
        if (_nativeAudio?.IsCapturing == true) return;
        _nativeAudio?.Dispose(); _nativeAudio = null;
        _sensitivityActive = true; ApplyGain();
        _sensa.Features.ClearForSilence();
        try
        {
            _nativeAudio = new AudioHapticsBridge(new SensaAudioSink(_sensa.Features));
            _nativeAudio.Start();
            DiagnosticsService.Info("Native mode: palm haptics from PC sound ('" + _nativeAudio.DeviceName + "'); game HD PCM not used.");
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Native mode audio haptics", ex);
            _nativeAudio?.Dispose(); _nativeAudio = null;
        }
    }

    private void StopNativeAudio()
    {
        if (_nativeAudio == null) return;
        _nativeAudio.Dispose(); _nativeAudio = null;
        _sensitivityActive = false; ApplyGain();
        _sensa.Features.ClearForSilence();
    }

    public void Stop()
    {
        StopNativeAudio();
        _virtual?.Stop();
        _sensitivityActive = false;
        ApplyGain();
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
