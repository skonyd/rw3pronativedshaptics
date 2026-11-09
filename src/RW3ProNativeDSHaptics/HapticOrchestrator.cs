using System;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Runs the selected mode: which output path starts (Sensa / Xbox body motors / virtual Xbox / classic
/// XInput / USB DualSense loopback / virtual DualSense), its auto-start and retry timing, failure
/// handling, the CPU guard and the liveness watchdog. Extracted from MainWindow, which keeps only the UI;
/// UI-dependent inputs arrive through the delegates below, decisions come from <see cref="HapticModePolicy"/>.
/// All members run on the UI thread (as before the extraction).
/// </summary>
public sealed class HapticOrchestrator
{
    private readonly NativeBridgeService _bridge;
    private readonly XInputHaptics _rumble;
    private readonly UserSettings _settings;

    // ── inputs from the UI ──
    public Func<HapticMode> SelectedMode { get; init; } = () => HapticMode.Normal;
    /// <summary>A test or setup step owns the outputs right now.</summary>
    public Func<bool> Busy { get; init; } = () => false;
    public Func<bool> Exiting { get; init; } = () => false;
    public Func<bool> IncludeXboxGameRumble { get; init; } = () => false;
    public Func<bool> IncludeXboxGameTriggers { get; init; } = () => false;
    public Action<bool> SetAudioPriority { get; init; } = _ => { };
    /// <summary>Restart elevated with an argument (task first, UAC fallback). True = this process is exiting.</summary>
    public Func<string, bool> RestartElevated { get; init; } = _ => false;
    /// <summary>Start the virtual DualSense bridge in this (elevated) process.</summary>
    public Func<Task> StartVirtualBridge { get; init; } = () => Task.CompletedTask;
    /// <summary>State changed: redraw.</summary>
    public Action Changed { get; init; } = () => { };
    /// <summary>Asked once when the user picks the native mode and nothing can start the bridge without a
    /// prompt: explain, set up the UAC-free task (one UAC) and start through it. True = this process exits.</summary>
    public Func<Task<bool>> OfferBridgeSetup { get; init; } = () => Task.FromResult(false);

    /// <summary>The user just picked the native mode (not an app start): a one-time setup prompt is allowed.</summary>
    public bool UserPickedNative { get; set; }

    // ── state ──
    public RunState Run { get; set; } = RunState.Off;
    public bool Starting { get; private set; }
    public long NextAutoStartTick { get; set; }
    public string? LastError { get; set; }
    public string? NativeWaitNote { get; set; }
    public WolverineMode AudioRoute { get; private set; } = WolverineMode.Unknown;
    public AudioHapticsBridge? XboxAudio { get; private set; }
    public XboxAudioGameBridge? CombinedXbox { get; private set; }

    public bool UseXboxGameBridge => IncludeXboxGameRumble() || IncludeXboxGameTriggers();

    public HapticOrchestrator(NativeBridgeService bridge, XInputHaptics rumble, UserSettings settings)
    {
        _bridge = bridge; _rumble = rumble; _settings = settings;
    }

    /// <summary>Mode re-selected or an option changed: forget the old error and try at once.</summary>
    public void ResetRetry()
    {
        LastError = null;
        NativeWaitNote = null;
        NextAutoStartTick = 0;
    }

    /// <summary>Starts the selected mode if it is not running (no-op for Normal, during tests, or before the retry time).</summary>
    public async Task EnsureRunningAsync()
    {
        var mode = SelectedMode();
        if (!HapticModePolicy.MayAutoStart(mode, Run, Starting, Busy(), Exiting(), Environment.TickCount64, NextAutoStartTick,
                _settings.AudioOutputEnabled, _settings.XboxNativeMix.PcmEnabled)) return;
        bool startVirtual = false;
        Starting = true;
        try
        {
            if (mode == HapticMode.Audio)
            {
                if (!await StartAudioAsync(mode)) return;
            }
            else if (await Task.Run(NativeBridgeService.FindDualSenseEndpoint) == null)
            {
                // No USB DualSense: the virtual bridge is the native source. Started after the finally block -
                // a `return` here would skip it (2026-11-09 bug: the mode then waited forever).
                startVirtual = true;
            }
            else
            {
                await _bridge.StartBridgeAsync();
                if (Superseded(mode) || !_settings.XboxNativeMix.PcmEnabled) { _bridge.Stop(); return; }
                Run = RunState.Native;
            }
            if (!startVirtual)
            {
                LastError = null;
                DiagnosticsService.Info("Mode " + mode + " running.");
            }
        }
        catch (Exception ex)
        {
            StopOutputs();
            if (LastError != ex.Message) DiagnosticsService.Error(mode + " auto start", ex);
            LastError = ex.Message;
            NextAutoStartTick = HapticModePolicy.RetryAfterFailure(mode, UseXboxGameBridge, Environment.TickCount64);
        }
        finally
        {
            Starting = false;
            Changed();
        }
        if (startVirtual) await StartVirtualAutomaticallyAsync();
    }

    private bool Superseded(HapticMode mode) => Busy() || Exiting() || SelectedMode() != mode;

    /// <summary>Audio owns body motors only; native trigger interpretation is the other mode.</summary>
    private async Task<bool> StartAudioAsync(HapticMode mode)
    {
        AudioRoute = await Task.Run(WolverineModeDetector.Detect);
        if (Superseded(mode) || !_settings.AudioOutputEnabled) return false;
        var profile = _settings.AudioProfile;
        if (UseXboxGameBridge)
        {
            if (AudioRoute != WolverineMode.Xbox)
                throw new InvalidOperationException("Birleşik Xbox çıkışı için Wolverine Xbox modunda olmalı.");
            var combined = new XboxAudioGameBridge
            {
                OutputLevel = _settings.OutputLevelPercent,
                IncludeGameRumble = IncludeXboxGameRumble(),
                IncludeGameTriggers = IncludeXboxGameTriggers()
            };
            await Task.Run(() => combined.Start(profile));
            if (Superseded(mode) || !_settings.AudioOutputEnabled || !UseXboxGameBridge || profile != _settings.AudioProfile)
            {
                await Task.Run(combined.Dispose);
                return false;
            }
            CombinedXbox = combined;
            combined.OutputLevel = _settings.OutputLevelPercent;
            combined.IncludeGameRumble = IncludeXboxGameRumble();
            combined.IncludeGameTriggers = IncludeXboxGameTriggers();
        }
        else if (AudioRoute == WolverineMode.Pc)
        {
            await _bridge.StartAudioToSensaAsync(profile: profile);
            if (Superseded(mode) || !_settings.AudioOutputEnabled || profile != _settings.AudioProfile) { _bridge.Stop(); return false; }
        }
        else if (AudioRoute == WolverineMode.Xbox || profile != AudioHapticProfile.Existing)
        {
            XboxAudio = new AudioHapticsBridge(new XboxAudioSink { OutputLevelPercent = _settings.OutputLevelPercent, PreferredIndex = _settings.PreferredXInputSlot }) { Profile = profile };
            XboxAudio.Start();
        }
        else
        {
            _rumble.StartAudio();          // unknown controller: classic XInput rumble
        }
        SetAudioPriority(true);
        Run = RunState.Audio;
        return true;
    }

    /// <summary>Native mode without a USB DualSense: start the virtual bridge without asking when possible
    /// (already elevated, or the UAC-free task is set up); otherwise wait for the user instead of retrying.</summary>
    public async Task StartVirtualAutomaticallyAsync()
    {
        if (Busy() || Starting || Exiting() || Run != RunState.Off || SelectedMode() != HapticMode.Native) return;
        bool installed = (await Task.Run(VirtualControllerSetup.Query)).Ready;
        bool taskReady = installed && !VirtualDualSenseBridge.IsElevated &&
                         await Task.Run(() => ElevatedLaunchTask.Query()) == ElevatedLaunchTask.TaskState.Ready;
        var plan = HapticModePolicy.PlanNativeSource(false, installed, VirtualDualSenseBridge.IsElevated, taskReady);
        string? note = HapticModePolicy.WaitNote(plan);
        if (plan == NativeSourcePlan.VirtualHere) { await StartVirtualBridge(); return; }
        if (plan == NativeSourcePlan.VirtualViaTask)
        {
            if (RestartElevated(App.StartVirtualDualSenseArg)) return;
            note = "UAC'siz başlatma görevi çalıştırılamadı; köprüyü elle başlat.";
        }
        if (plan == NativeSourcePlan.WaitForClick && UserPickedNative)
        {
            UserPickedNative = false;   // ask at most once per selection; never on an app start
            if (await OfferBridgeSetup()) return;
        }
        NextAutoStartTick = HapticModePolicy.Never;   // wait for the user; re-selecting the mode tries again
        if (note != NativeWaitNote) { NativeWaitNote = note; DiagnosticsService.Info("Native mode waiting: " + note); }
        Changed();
    }

    /// <summary>Stops every output this class started (not the user's tests).</summary>
    public void StopOutputs()
    {
        XboxAudio?.Dispose();
        XboxAudio = null;
        try { CombinedXbox?.Dispose(); }
        catch (Exception ex) { DiagnosticsService.Error("Xbox köprüsü geri alma", ex); }
        CombinedXbox = null;
        _rumble.StopAudio();
        SetAudioPriority(false);
        _bridge.Stop();
        Run = RunState.Off;
    }

    /// <summary>Applies the Xbox feedback options to a running virtual-Xbox audio bridge. False = not running.</summary>
    public bool UpdateCombinedOptions()
    {
        if (CombinedXbox == null || !UseXboxGameBridge) return false;
        CombinedXbox.IncludeGameRumble = IncludeXboxGameRumble();
        CombinedXbox.IncludeGameTriggers = IncludeXboxGameTriggers();
        return true;
    }

    /// <summary>Periodic guard (UI timer): CPU budget, dead audio capture, dead native bridge, auto-run.</summary>
    public void Tick(PerformanceMonitor performance, int tickCount)
    {
        if (tickCount % 8 == 0)
        {
            performance.Sample();
            if (Run == RunState.Audio && performance.SustainedHighSamples >= 3)
            {
                StopOutputs();
                LastError = "İşlemci sınırı (%3) üst üste aşıldı; sesten titreşim 1 dk durduruldu.";
                NextAutoStartTick = Environment.TickCount64 + HapticModePolicy.CpuBackoffMs;
                DiagnosticsService.Info(LastError);
            }
        }
        bool audioAlive = AudioRoute switch
        {
            WolverineMode.Pc => _bridge.IsAudioToSensaRunning,
            WolverineMode.Xbox => CombinedXbox?.IsRunning ?? (XboxAudio?.IsCapturing == true),
            _ => XboxAudio?.IsCapturing ?? _rumble.Snapshot().IsCapturing
        };
        if (Run == RunState.Audio && !audioAlive)
        {
            LastError = (AudioRoute switch
            {
                WolverineMode.Pc => _bridge.AudioFailure,
                WolverineMode.Xbox => CombinedXbox?.Failure ?? XboxAudio?.Failure,
                _ => XboxAudio?.Failure ?? _rumble.Snapshot().LastError
            }) ?? "Ses yakalama beklenmedik biçimde durdu.";
            StopOutputs();
            NextAutoStartTick = HapticModePolicy.RetryAfterFailure(HapticMode.Audio, UseXboxGameBridge, Environment.TickCount64);
        }
        if (Run == RunState.Native && !Busy() && !_bridge.IsBridgeRunning && !_bridge.IsVirtualDualSenseRunning)
        {
            LastError = "Native köprü durdu: " + _bridge.SensaStatus + " / " + _bridge.SourceStatus;
            DiagnosticsService.Info(LastError);
            StopOutputs();
            NextAutoStartTick = Environment.TickCount64 + HapticModePolicy.RetryMs;
        }
        if (tickCount % 4 == 0) _ = EnsureRunningAsync();   // auto-run / resume after tests
    }
}
