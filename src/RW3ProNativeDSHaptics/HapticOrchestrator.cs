using System;
using System.Collections.Generic;
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
    /// <summary>The selector's plan (<see cref="OutputMatrix.Plan"/>): virtual pad profile, palms, game rumble/triggers.</summary>
    public Func<OutputPlan> Plan { get; init; } = () => OutputMatrix.Plan(GameOutput.Physical, OutputFeature.None);
    private bool IncludeXboxGameRumble() => Plan().Rumble;
    private bool IncludeXboxGameTriggers() => Plan().Triggers;
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
    /// <summary>Xbox-mode audio to the palms: body-motor companion for while the palms are blocked (ControllerFocusGuard).</summary>
    public AudioHapticsBridge? XboxCompanion { get; private set; }
    public XboxAudioGameBridge? CombinedXbox { get; private set; }

    /// <summary>A virtual Xbox 360 / Series / PS4 pad is selected: the game sees it, not the Wolverine.</summary>
    public bool UseXboxGameBridge => Plan().VirtualProfile != null;
    private bool _virtualPalms, _restartingPalms;

    private async Task RestartPalmsAsync()
    {
        try { await _bridge.StartAudioToSensaAsync(profile: EffectiveProfile); }
        catch (Exception ex) { DiagnosticsService.Info("Virtual pad: palm audio restart failed: " + ex.Message); await Task.Delay(2000); }
        finally { _restartingPalms = false; }
    }

    // Games that had the real pad open before a virtual pad appeared (PadHolders): they keep it until restarted.
    private volatile List<(int Pid, string Name)> _padHolders = new();
    /// <summary>"Restart X" while a game opened before the virtual pad still runs, else "turn off the other pad" while a
    /// foreign pad is on XInput next to the strict virtual pad; null otherwise.</summary>
    public string? PadHolderNote
    {
        get
        {
            var list = _padHolders;
            if (list.Count == 0)
                return XboxAudioGameBridge.ForeignPads is { } other
                    ? "Başka bir kol da bağlı (" + other + "; örneğin Bluetooth ile eşli bir Xbox kolu). Oyun sanal kol yerine onu " +
                      "kullanabilir: o kolu kapat (Xbox tuşuna 6 sn bas) ya da Bluetooth'tan bağlantısını kes, sonra oyunu yeniden aç."
                    : null;
            return string.Join(", ", list.ConvertAll(h => h.Name)) + " sanal koldan önce açıldı ve hâlâ gerçek kolu kullanıyor " +
                  "(girdi, rumble ve tetik RW3'ü atlar, Xbox modunda avuçlar susar). Oyunu kapatıp yeniden aç.";
        }
    }

    /// <summary>Looks (in the background) for games started before <paramref name="since"/> that hold a pad.</summary>
    public void CheckPadHolders(DateTime since) => Task.Run(() =>
    {
        var found = PadHolders.Find(since);
        _padHolders = found;
        if (found.Count > 0) DiagnosticsService.Info("Virtual pad: opened before it and still holding the real pad: " +
            string.Join(", ", found.ConvertAll(h => $"{h.Name} (pid {h.Pid})")) + ".");
    });

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
        var source = HapticModePolicy.ResolveSource(_settings.NativeHapticSource, _settings.XboxNativeMix.PcmEnabled);
        var mix = _settings.XboxNativeMix;
        bool nativeAny = source != NativeHapticSource.Off || mix.RumbleEnabled || mix.TriggersEnabled;
        if (!HapticModePolicy.MayAutoStart(mode, Run, Starting, Busy(), Exiting(), Environment.TickCount64, NextAutoStartTick,
                _settings.AudioOutputEnabled, nativeAny)) return;
        bool startVirtual = false;
        Starting = true;
        try
        {
            if (mode == HapticMode.Audio)
            {
                if (!await StartAudioAsync(mode)) return;
            }
            else if (!HapticModePolicy.UseUsbDualSense(await Task.Run(NativeBridgeService.FindDualSenseEndpoint) != null, source))
            {
                // No USB DualSense (or a source it cannot give): the virtual bridge is the native source. Started after
                // the finally block - a `return` here would skip it (2026-11-09 bug: the mode then waited forever).
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
            NextAutoStartTick = RetryAfterFailure(mode);
        }
        finally
        {
            Starting = false;
            Changed();
        }
        if (startVirtual) await StartVirtualAutomaticallyAsync();
    }

    /// <summary>The virtual Xbox pad is never retried by a non-elevated RW3 (each try would ask for UAC). Already elevated
    /// (the user started it), it retries every 10 s: a replug or mode switch makes detection miss for a few seconds, and
    /// until 2026-11-15 one miss left the game on the physical pad for the rest of the session.</summary>
    private long RetryAfterFailure(HapticMode mode) =>
        mode == HapticMode.Audio && UseXboxGameBridge && VirtualDualSenseBridge.IsElevated
            ? Environment.TickCount64 + 10_000
            : HapticModePolicy.RetryAfterFailure(mode, UseXboxGameBridge, Environment.TickCount64);

    private bool Superseded(HapticMode mode) => Busy() || Exiting() || SelectedMode() != mode;

    /// <summary>The frequency filter in use. Melody output needs the full band: a bass-only filter would remove the
    /// melody before the analyser hears it.</summary>
    private AudioHapticProfile EffectiveProfile =>
        _settings.OutputMethod == (int)SensaOutputMethod.Melody ? AudioHapticProfile.Existing : _settings.AudioProfile;

    /// <summary>Audio owns body motors only; native trigger interpretation is the other mode.</summary>
    private async Task<bool> StartAudioAsync(HapticMode mode)
    {
        AudioRoute = await Task.Run(WolverineModeDetector.Detect);
        // Right after a virtual pad stops (another pad type, HidHide undone) the pad re-enumerates for a moment.
        for (int i = 0; i < 6 && UseXboxGameBridge && AudioRoute is not (WolverineMode.Xbox or WolverineMode.Pc) && !Superseded(mode); i++)
        {
            await Task.Delay(1000);
            AudioRoute = await Task.Run(WolverineModeDetector.Detect);
        }
        if (Superseded(mode) || !_settings.AudioOutputEnabled) return false;
        var profile = EffectiveProfile;
        if (UseXboxGameBridge)
        {
            var plan = Plan();
            if (AudioRoute is not (WolverineMode.Xbox or WolverineMode.Pc))
                throw new InvalidOperationException("Wolverine bulunamadı; kolu bağla.");
            if (!VirtualDualSenseBridge.IsElevated)
            {
                // The virtual pad and HidHide need admin: the UAC-free task first (as the DualSense bridge does).
                if (RestartElevated(App.StartVirtualDualSenseArg)) return false;
                throw new InvalidOperationException("Sanal kol için yönetici izni gerekli.");
            }
            // Palms: PC mode over USB, Xbox mode over GIP (needs the Xbox-mode Sensa setting).
            bool strict = true, palms = plan.Palms && (AudioRoute == WolverineMode.Pc || XboxSensa.Enabled);
            var combined = new XboxAudioGameBridge
            {
                Strict = strict,
                VirtualProfile = plan.VirtualProfile!,
                EngineProbe = () => _bridge.SensaEngineSummary,
                OutputLevel = _settings.OutputLevelPercent,
                IncludeGameRumble = IncludeXboxGameRumble(),
                IncludeGameTriggers = IncludeXboxGameTriggers()
            };
            var padCreated = DateTime.Now;
            await Task.Run(() => combined.Start(profile));
            if (Superseded(mode) || !_settings.AudioOutputEnabled || !UseXboxGameBridge || profile != EffectiveProfile)
            {
                await Task.Run(combined.Dispose);
                return false;
            }
            CombinedXbox = combined;
            _virtualPalms = palms;
            combined.SynthTriggers = XboxSensa.SynthTriggers = plan.SynthTriggers;
            CheckPadHolders(padCreated);
            if (palms)
            {
                // Strict: the sound goes to the palms (Sensa engine over GIP); the virtual pad bridge only does input,
                // the game's rumble (XInput body) and the game's triggers (Sensa frame).
                await _bridge.StartAudioToSensaAsync(profile: profile);
                if (Superseded(mode)) { await Task.Run(combined.Dispose); CombinedXbox = null; _bridge.Stop(); return false; }
            }
            combined.OutputLevel = _settings.OutputLevelPercent;
            combined.IncludeGameRumble = IncludeXboxGameRumble();
            combined.IncludeGameTriggers = IncludeXboxGameTriggers();
        }
        else if (AudioRoute == WolverineMode.Pc || (AudioRoute == WolverineMode.Xbox && XboxSensa.Enabled))
        {
            // PC mode: Sensa over USB. Xbox mode (XboxSensa): the same engine reaches the palms over GIP, and the
            // calibration rated palms better than body rumble (E-20261111-07). The game's own rumble/triggers stay its own.
            // The frequency filter applies on this path too (as in PC mode). Until 2026-11-14 any filter other than
            // "full band" silently moved Xbox-mode audio from the palms to the body motors.
            await _bridge.StartAudioToSensaAsync(profile: profile);
            if (Superseded(mode) || !_settings.AudioOutputEnabled || profile != EffectiveProfile) { _bridge.Stop(); return false; }
            if (AudioRoute == WolverineMode.Xbox)
            {
                // Palms follow Windows' controller focus: a browser or Electron app in front (Opera, Claude, Codex…)
                // drops them silently. The body motors (XInput) are not focus-bound, so they carry the sound meanwhile.
                XboxCompanion = new AudioHapticsBridge(new XboxAudioSink { OutputLevelPercent = _settings.OutputLevelPercent, PreferredIndex = _settings.PreferredXInputSlot, Companion = true }) { Profile = profile };
                try { XboxCompanion.Start(); }
                catch (Exception ex) { DiagnosticsService.Error("Xbox body companion", ex); XboxCompanion.Dispose(); XboxCompanion = null; }
            }
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
        // The checks above take a while: the user may have picked another mode meanwhile (2026-11-15: Audio picked during
        // this wait, then the virtual DualSense started anyway and flipped the label back to Native).
        if (Busy() || Starting || Exiting() || Run != RunState.Off || SelectedMode() != HapticMode.Native) return;
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
        XboxCompanion?.Dispose();
        XboxCompanion = null;
        _padHolders = new();
        XboxSensa.SynthTriggers = false;
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
        CombinedXbox.SynthTriggers = XboxSensa.SynthTriggers = Plan().SynthTriggers;
        return true;
    }

    /// <summary>Periodic guard (UI timer): CPU budget, dead audio capture, dead native bridge, auto-run.</summary>
    public void Tick(PerformanceMonitor performance, int tickCount)
    {
        if (tickCount % 8 == 0)
        {
            if (_padHolders.Count > 0) _padHolders = _padHolders.FindAll(h => PadHolders.Alive(h.Pid));
            performance.Sample();
            if (Run == RunState.Audio && performance.SustainedHighSamples >= 3 && CombinedXbox != null)
            {
                // The virtual Xbox pad carries the game's controller input: stopping it mid-game hands the game back to the
                // physical pad (2026-11-15: stopped 5 s after start by start-up CPU). Log only.
                if (tickCount % 480 == 0)
                    DiagnosticsService.Info($"CPU {performance.CpuPercent:F1}% over the {PerformanceMonitor.CpuBudgetPercent}% budget; virtual Xbox pad kept running.");
            }
            else if (Run == RunState.Audio && performance.SustainedHighSamples >= 3)
            {
                StopOutputs();
                LastError = "İşlemci sınırı (%3) üst üste aşıldı; sesten titreşim 1 dk durduruldu.";
                NextAutoStartTick = Environment.TickCount64 + HapticModePolicy.CpuBackoffMs;
                DiagnosticsService.Info(LastError);
            }
        }
        // Virtual pad + palms: a palm audio capture that stops (the foreground app changes, a headset reconnects) is
        // restarted alone. Until 2026-11-22 it stopped the whole bridge, which undid the hide and handed the game the
        // real pad mid-session (E-20261122-03).
        if (Run == RunState.Audio && CombinedXbox is { IsRunning: true } && _virtualPalms && !_bridge.IsAudioToSensaRunning && !_restartingPalms)
        {
            _restartingPalms = true;
            DiagnosticsService.Info("Virtual pad: palm audio stopped (" + (_bridge.AudioFailure ?? "?") + "); restarting it, the pad keeps running.");
            _ = RestartPalmsAsync();
        }
        bool audioAlive = AudioRoute switch
        {
            // the virtual pad is alive as long as its bridge runs; a dropped palm audio capture is restarted on its own below
            WolverineMode.Xbox or WolverineMode.Pc when CombinedXbox is { } c => c.IsRunning,
            WolverineMode.Pc => _bridge.IsAudioToSensaRunning,
            WolverineMode.Xbox => XboxAudio?.IsCapturing ?? _bridge.IsAudioToSensaRunning,
            _ => XboxAudio?.IsCapturing ?? _rumble.Snapshot().IsCapturing
        };
        if (Run == RunState.Audio && !audioAlive)
        {
            LastError = (AudioRoute switch
            {
                WolverineMode.Xbox or WolverineMode.Pc when CombinedXbox != null => CombinedXbox.Failure ?? (_virtualPalms ? _bridge.AudioFailure : null),
                WolverineMode.Pc => _bridge.AudioFailure,
                WolverineMode.Xbox => XboxAudio?.Failure ?? _bridge.AudioFailure,
                _ => XboxAudio?.Failure ?? _rumble.Snapshot().LastError
            }) ?? "Ses yakalama beklenmedik biçimde durdu.";
            DiagnosticsService.Info("Audio mode stopped by the liveness check: " + LastError);
            StopOutputs();
            NextAutoStartTick = RetryAfterFailure(HapticMode.Audio);
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
