using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Main window: three top tabs (Genel kullanım / Test / Ayarlar), no scrolling.
/// Evidence rules are unchanged: an API call or a started engine is never shown as
/// physical success; only the user's "Hissettim" answer records a felt pulse.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly UserSettings _settings;
    private readonly XInputHaptics _rumble = new();
    private readonly UpdateService _updates = new();
    private readonly NativeBridgeService _bridge = new();
    private readonly PerformanceMonitor _performance = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _normalMenu, _audioMenu, _nativeMenu;

    private bool _busy, _initialized, _realExit;
    // Mode orchestration lives in HapticOrchestrator; these forward to it so the UI code reads as before.
    private HapticOrchestrator _orch = null!;
    private RunState _run { get => _orch.Run; set => _orch.Run = value; }
    private string? _lastErrorNoted { get => _orch.LastError; set => _orch.LastError = value; }
    private UpdateInfo? _availableUpdate;
    private ProcessPriorityClass? _originalPriority;
    private int _tickCount;
    private CancellationTokenSource? _testCancel;

    // Test tab state
    private HapticTestChannel? _askingSide;
    private ushort _prevPadButtons;
    private readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x93, 0xA2, 0xBF));

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += CalibKeyDown;
        var demoChoices = new List<ChannelLabScene>();
        foreach (var demo in NativeDemoSignals.All)
            demoChoices.Add(new ChannelLabScene("demo:" + demo, NativeDemoSignals.Title(demo), demo));
        DemoSceneCombo.ItemsSource = demoChoices;
        GameSimCombo.ItemsSource = GameSignalSimulator.Scenes;
        GameSimCombo.SelectedIndex = 0;
        DemoSceneCombo.SelectedIndex = 0;
        ChannelSceneCombo.ItemsSource = DualSenseChannelFixtures.Scenes;
        ChannelSceneCombo.SelectedIndex = 0;
        _settings = _store.Load();
        _orch = new HapticOrchestrator(_bridge, _rumble, _settings)
        {
            SelectedMode = () => SelectedMode,
            Busy = () => _busy,
            Exiting = () => _realExit,
            IncludeXboxGameRumble = () => IncludeXboxGameRumble.IsChecked == true,
            IncludeXboxGameTriggers = () => IncludeXboxGameTriggers.IsChecked == true,
            SetAudioPriority = SetAudioPriority,
            RestartElevated = RestartElevated,
            StartVirtualBridge = StartVirtualBridgeAsync,
            OfferBridgeSetup = OfferBridgeSetupAsync,
            Changed = RefreshStatus
        };
        _settings.OutputLevelPercent = HapticOutputLevel.Limit(_settings.OutputLevelPercent);
        _rumble.PreferredIndex = _settings.PreferredXInputSlot;
        _rumble.OutputLevelPercent = _settings.OutputLevelPercent;
        _bridge.OutputLevelPercent = _settings.OutputLevelPercent;
        _bridge.SourceGainPercent = _settings.NativeGainPercent;
        if (_settings.ExperimentalMultiband && _settings.OutputMethod == 0) _settings.OutputMethod = (int)SensaOutputMethod.Multiband;
        _settings.ExperimentalMultiband = false;
        _settings.OutputMethod = Math.Clamp(_settings.OutputMethod, 0, 2);
        _bridge.OutputMethod = (SensaOutputMethod)_settings.OutputMethod;
        _settings.Transport = Math.Clamp(_settings.Transport, 0, 1);
        // Standalone install: without the licensed Razer DLLs the Synapse path cannot run, Direct USB can.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "HAR.dll")) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "Interhaptics.RazerProvider.dll")))
        {
            if (_settings.Transport != 1) DiagnosticsService.Info("Razer DLLs not present: using Direct USB.");
            _settings.Transport = 1;
            TransportSynapse.IsEnabled = false;
        }
        _bridge.Transport = (SensaTransport)_settings.Transport;
        (_settings.Transport == 1 ? TransportDirect : TransportSynapse).IsChecked = true;
        _bridge.AudioSensitivityPercent = _settings.AudioSensitivityPercent;
        _bridge.HidePadInGames = _settings.HidePadInGames;
        _settings.TriggerFeel = Math.Clamp(_settings.TriggerFeel, 0, 1);
        _bridge.TriggerMotors = _settings.TriggerFeel == 1;
        _bridge.PalmTriggerFeel = _settings.PalmTriggerFeel;
        PalmTriggerFeel.IsChecked = _settings.PalmTriggerFeel;
        BodyMotorLimit.Enabled = _settings.BigMotorCeiling;
        BigMotorCeiling.IsChecked = _settings.BigMotorCeiling;
        XboxSensa.Enabled = _settings.XboxSensa;
        XboxSensaOutput.IsChecked = _settings.XboxSensa;
        RouteImpulse.IsChecked = _bridge.TriggerMotors;
        (_settings.TriggerFeel == 1 ? TriggerFeelMotors : TriggerFeelClassic).IsChecked = true;
        var mix = _settings.XboxNativeMix ?? new XboxNativeMix();
        RoutePcm.IsChecked = mix.PcmEnabled;
        RouteRumble.IsChecked = mix.RumbleEnabled;
        RouteTriggers.IsChecked = mix.TriggersEnabled;
        RouteAudio.IsChecked = _settings.AudioOutputEnabled;
        (_settings.AudioProfile switch
        {
            AudioHapticProfile.Controlled => AudioProfileControlled,
            AudioHapticProfile.Balanced => AudioProfileBalanced,
            AudioHapticProfile.Dynamic => AudioProfileDynamic,
            _ => AudioProfileExisting
        }).IsChecked = true;
        XboxBassTreble.IsChecked = _settings.XboxBassTreble;
        AudioLevelShaper.AutoLevelEnabled = _settings.AudioAutoLevel;
        AudioLevelShaper.HumSuppressionEnabled = _settings.AudioHumSuppression;
        AudioAutoLevel.IsChecked = _settings.AudioAutoLevel;
        AudioHumSuppression.IsChecked = _settings.AudioHumSuppression;
        SensaStereoPrototype.DirectTransientRecords = _settings.DirectTransientRecords;
        DirectTransientRecords.IsChecked = _settings.DirectTransientRecords;
        XboxPcmLevel.Value = HapticOutputLevel.Limit(mix.PcmPercent);
        XboxRumbleLevel.Value = HapticOutputLevel.Limit(mix.RumblePercent);
        XboxTriggerLevel.Value = HapticOutputLevel.Limit(mix.TriggerPercent);
        XboxBodyMixPriority.SelectedIndex = Math.Clamp((int)mix.Priority, 0, 2);
        ApplyXboxMix();
        HhHideCheckbox.IsChecked = _settings.HidePadInGames;
        ((SensaOutputMethod)_settings.OutputMethod switch
            { SensaOutputMethod.Hybrid => MethodHybrid, SensaOutputMethod.Multiband => MethodMultiband, _ => MethodStandard }).IsChecked = true;
        (_settings.AudioSensitivityPercent >= 250 ? SensMax : _settings.AudioSensitivityPercent >= 160 ? SensHigh : SensNormal).IsChecked = true;
        UpdateOutputMethodStatus();
        AudioHapticsBridge.SyncDelaySetting = _settings.AudioSyncDelayMs;

        try { using var self = Process.GetCurrentProcess(); _originalPriority = self.PriorityClass; }
        catch (Exception ex) { DiagnosticsService.Error("Priority lookup", ex); }

        SelectLevelPreset(_settings.OutputLevelPercent);
        (_settings.NativeGainPercent >= 300 ? GainMax : _settings.NativeGainPercent >= 200 ? GainStrong : GainNormal).IsChecked = true;
        (_settings.AudioSyncDelayMs switch { < 0 => SyncAuto, 0 => Sync0, <= 150 => Sync150, <= 300 => Sync300, _ => Sync450 }).IsChecked = true;
        UpdateAudioSyncStatus();
        DeviceSlotCombo.SelectedIndex = _settings.PreferredXInputSlot is >= -1 and <= 3
            ? _settings.PreferredXInputSlot + 1 : 0;
        AutostartCheckbox.IsChecked = StartupService.IsEnabled;
        VersionText.Text = "v" + UpdateService.InstalledVersion;
        InitializeTray();

        var startupArgs = Environment.GetCommandLineArgs();
        bool startXboxAudioBridge = VirtualDualSenseBridge.IsElevated &&
            startupArgs.Contains(App.StartXboxAudioBridgeArg, StringComparer.OrdinalIgnoreCase);
        if (startXboxAudioBridge)
        {
            bool triggersOnly = startupArgs.Contains("--xbox-triggers-only", StringComparer.OrdinalIgnoreCase);
            IncludeXboxGameRumble.IsChecked = !triggersOnly;
            IncludeXboxGameTriggers.IsChecked = triggersOnly;
        }

        ((startXboxAudioBridge ? HapticMode.Audio : _settings.Mode) switch
        {
            HapticMode.Audio => ModeAudio,
            HapticMode.Native => ModeNative,
            _ => ModeNormal
        }).IsChecked = true;
        _initialized = true;
        if (startXboxAudioBridge)
            Dispatcher.BeginInvoke(new Action(async () => { await EnsureModeRunningAsync(); }));
        else if (startupArgs.Contains(App.StartVirtualDualSenseArg, StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(new Action(async () => { await StartVirtualBridgeAsync(); }));

        ShowFeelResults();
        _timer.Tick += Timer_Tick;
        _padTimer.Tick += PadTimer_Tick;
        _padTimer.Start();
        _timer.Start();
        RefreshStatus();
        DiagnosticsService.Info("Application opened; selected mode " + _settings.Mode + " runs automatically.");
    }

    // ───────────────────────── tray ─────────────────────────

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("RW3 Pro Haptics • Aç", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _normalMenu = new Forms.ToolStripMenuItem("Normal", null, (_, _) => Dispatcher.Invoke(() => ModeNormal.IsChecked = true));
        _audioMenu = new Forms.ToolStripMenuItem("Sesten titreşim", null, (_, _) => Dispatcher.Invoke(() => ModeAudio.IsChecked = true));
        _nativeMenu = new Forms.ToolStripMenuItem("Native DualSense (deneysel)", null, (_, _) => Dispatcher.Invoke(() => ModeNative.IsChecked = true));
        menu.Items.Add(_normalMenu);
        menu.Items.Add(_audioMenu);
        menu.Items.Add(_nativeMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Dispatcher.Invoke(ExitCompletely));
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/App.ico")).Stream;
        using var embeddedIcon = new System.Drawing.Icon(iconStream);
        _trayIcon = (System.Drawing.Icon)embeddedIcon.Clone();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "RW3 Pro Haptics",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
    }

    // ───────────────────────── window lifecycle ─────────────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshChecksAsync();
        try
        {
            await RefreshChannelEndpointsAsync();
            ChannelMicCombo.ItemsSource = await Task.Run(ChannelLabRunner.Microphones);
            ChannelMicCombo.SelectedIndex = -1; // Require an explicit microphone choice, never guess a streaming device.
        }
        catch (Exception ex) { ChannelStatus.Text = "Mikrofon listesi okunamadı: " + ex.Message; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_realExit) return;
        e.Cancel = true;
        Hide();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void ExitCompletely()
    {
        _realExit = true;
        _timer.Stop();
        _testCancel?.Cancel();
        while (_starting) await Task.Delay(25);
        StopAll();
        await Task.Yield();
        _bridge.Dispose();
        _rumble.Dispose();
        _performance.Dispose();
        DiagnosticsService.Info("Application exit; motors stopped.");
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose(); _trayIcon = null;
        System.Windows.Application.Current.Shutdown();
    }

    public void RecoverFromUnexpectedError(Exception failure)
    {
        try { StopAll(); } catch { }
        _lastErrorNoted = "Beklenmeyen hata: " + failure.Message;
        try { RefreshStatus(); } catch { }
    }

    // ───────────────────────── tabs ─────────────────────────

    private async void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPage == null) return;
        GeneralPage.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TestPage.Visibility = TabTest.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabGeneral.IsChecked == true && _initialized) await RefreshChecksAsync();
        if (TabSettings.IsChecked == true && _initialized) await RefreshVirtualControllerAsync();
    }

    // ───────────────────────── Ayarlar: virtual DualSense (HIDMaestro) ─────────────────────────

    private bool _vcBusy;

    /// <summary>First pick of the native mode without the UAC-free task: one explanation, one UAC prompt,
    /// then the bridge starts through the task (and does so without prompts from then on).</summary>
    private async Task<bool> OfferBridgeSetupAsync()
    {
        var answer = System.Windows.MessageBox.Show(
            "Oyunun DualSense titreşimi için sanal DualSense köprüsü yönetici izniyle çalışır." + Environment.NewLine + Environment.NewLine +
            "Şimdi bir kez izin verirsen köprü bundan sonra bu modu seçtiğinde ve uygulama açıldığında sormadan başlar " +
            "(Windows zamanlanmış görevi; Ayarlar → Sanal DualSense'ten kapatabilirsin).",
            "Oyunun DualSense titreşimi", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return false;
        var (_, msg) = await VirtualControllerSetup.RunElevatedAsync(ElevatedLaunchTask.InstallArg);
        DiagnosticsService.Info("Bridge task setup from mode pick: " + msg);
        return RestartElevated(App.StartVirtualDualSenseArg);
    }

    private async Task RefreshBridgeTaskAsync()
    {
        if (VcTaskText == null) return;
        var t = await Task.Run(() => ElevatedLaunchTask.Query());
        VcTaskText.Text = t switch
        {
            ElevatedLaunchTask.TaskState.Ready => "UAC'siz başlatma: ✓ açık (köprü izin sormadan başlar).",
            ElevatedLaunchTask.TaskState.OtherExe => "UAC'siz başlatma: başka bir derleme için kurulu; bu sürüm için yeniden etkinleştir.",
            _ => "UAC'siz başlatma: kapalı (her başlatmada Windows izin sorar)."
        };
        VcTaskBtn.Content = t == ElevatedLaunchTask.TaskState.Ready ? "Kapat" : "Etkinleştir";
        VcTaskBtn.Tag = t;
        VcTaskBtn.IsEnabled = !_vcBusy;
    }

    private async void VcTask_Click(object sender, RoutedEventArgs e)
    {
        if (_vcBusy) return;
        bool remove = VcTaskBtn.Tag is ElevatedLaunchTask.TaskState.Ready;
        _vcBusy = true; VcTaskBtn.IsEnabled = false;
        VcTaskText.Text = (remove ? "Kapatılıyor" : "Etkinleştiriliyor") + " (Windows bir kez yönetici izni isteyecek)…";
        try
        {
            var (_, msg) = await VirtualControllerSetup.RunElevatedAsync(remove ? ElevatedLaunchTask.UninstallArg : ElevatedLaunchTask.InstallArg);
            DiagnosticsService.Info("Bridge task " + (remove ? "removed" : "installed") + ": " + msg);
        }
        catch (Exception ex) { NoteError("Bridge task", ex); }
        _vcBusy = false;
        await RefreshBridgeTaskAsync();
    }

    private async Task RefreshVirtualControllerAsync(string? lastAction = null)
    {
        await RefreshBridgeTaskAsync();
        var st = await Task.Run(VirtualControllerSetup.Query);
        VcDriverText.Text = !st.SdkPresent ? "SDK: bu derlemede yok (tools/get-hidmaestro.ps1)" :
            "HIDMaestro " + st.SdkVersion + ": sürücü " + (st.DriverInstalled ? "✓" : "✗ kurulu değil") +
            " • USB/IP (titreşim sesi) " + (st.UsbipAvailable ? "✓" : "✗ yok") +
            (st.Error != null ? " — " + st.Error : "");
        var t = st.LastSelfTest;
        VcSelfTestText.Text = (lastAction != null ? lastAction + Environment.NewLine : "") +
            (t == null ? "Sağlık testi: henüz yapılmadı." : "Son sağlık testi (" + t.At.ToString("dd.MM HH:mm") + "): " + (t.Ok ? "✓ " : "✗ ") + t.Summary);
        bool healthy = st.Ready && t?.Ok == true;
        VcBadgeText.Text = !st.SdkPresent ? "SDK yok" : healthy ? "Sağlıklı" : st.Ready ? "Kurulu • test bekliyor" : st.DriverInstalled ? "Eksik kurulum" : "Kurulu değil";
        VcBadge.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(
            healthy ? "#1D4D38" : st.Ready ? "#3A2A10" : "#4D1D1D"));
        VcInstallBtn.Content = st.Ready ? "Yeniden kur" : "Kur";
        VcInstallBtn.IsEnabled = st.SdkPresent && !_vcBusy;
        VcTestBtn.IsEnabled = st.Ready && !_vcBusy;
        VcUninstallBtn.IsEnabled = st.SdkPresent && (st.DriverInstalled || st.UsbipAvailable) && !_vcBusy;
        await RefreshHidHideAsync();
        await RefreshPadModeAsync();
        UpdateBridgeStatus();
    }

    // Controller mode: PC (Sensa) or Xbox (impulse-trigger motors). PC → Xbox by software, back by key combo.
    private bool _padModeBusy;

    private async Task RefreshPadModeAsync(string? note = null)
    {
        if (PadModeText == null) return;
        var m = await Task.Run(WolverineModeControl.Detect);
        ApplyPadModeOptions(m);
        PadModeText.Text = (note != null ? note + Environment.NewLine : "") + m switch
        {
            WolverineControllerMode.Pc => "PC modu: avuçlarda Sensa HD titreşim; DualSense tetik efektleri gövde motorlarında. Tetiklerin içinde hissetmek için Xbox moduna geç.",
            WolverineControllerMode.Xbox => XboxSensa.Enabled
                ? "Xbox modu: avuçlarda Sensa (Razer GIP kanalı) + tetik motorları; DualSense tetik efektleri tetiklerin içinde, titreşim sesi avuçlarda. " + WolverineModeControl.PcComboHint
                : "Xbox modu ('Xbox modunda Sensa' kapalı): tetik efektleri tetik motorlarında, titreşim sesi gövde motorlarında. " + WolverineModeControl.PcComboHint,
            _ => "Kol bulunamadı (kablo ya da alıcıyla bağlı ve açık olmalı)."
        };
        PadModeXboxBtn.IsEnabled = m == WolverineControllerMode.Pc && !_padModeBusy;
    }

    private async void PadModeRefresh_Click(object sender, RoutedEventArgs e) => await RefreshPadModeAsync();

    // Xbox-only audio option: bass → heavy motor, treble → light motor. Usable and effective only while the
    // controller is detected in Xbox mode, so a saved choice can never act in PC mode (Sensa palms there).
    private WolverineControllerMode _padMode = WolverineControllerMode.NotFound;

    private void ApplyPadModeOptions(WolverineControllerMode m)
    {
        _padMode = m;
        bool xbox = m == WolverineControllerMode.Xbox;
        XboxBassTreble.IsEnabled = xbox && !XboxSensa.Enabled;
        XboxBodyEnvelope.Preferred = xbox && _settings.XboxBassTreble ? XboxMotorSplit.BassTreble : XboxMotorSplit.Stereo;
        XboxBassTrebleHint.Text = xbox && XboxSensa.Enabled
            ? "Xbox modunda ses avuçlara (Sensa) gidiyor; bu seçenek yalnız 'Xbox modunda Sensa' kapalıyken gövde motorlarını böler."
            : xbox
            ? (_settings.XboxBassTreble
                ? "Açık: bas (< 120 Hz) büyük/ağır motorda, tiz (> 300 Hz) küçük motorda. Sol/sağ yön yok."
                : "Kapalı: sol motor sol kanalı, sağ motor sağ kanalı izler (sol/sağ yön).")
            : "Yalnız kol Xbox modundayken seçilebilir" + (_settings.XboxBassTreble ? " (kayıtlı seçim PC modunda uygulanmaz)." : ".");
    }

    private async Task RefreshPadOptionsAsync()
    {
        try { ApplyPadModeOptions(await Task.Run(WolverineModeControl.Detect)); }
        catch (Exception ex) { NoteError("Kol modu", ex); }
    }

    private void DirectTransientRecords_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.DirectTransientRecords = DirectTransientRecords.IsChecked == true;
        SensaStereoPrototype.DirectTransientRecords = _settings.DirectTransientRecords;   // read every frame: applies live
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Darbe kaydı", ex); }
    }

    private void AudioShaping_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.AudioAutoLevel = AudioAutoLevel.IsChecked == true;
        _settings.AudioHumSuppression = AudioHumSuppression.IsChecked == true;
        AudioLevelShaper.AutoLevelEnabled = _settings.AudioAutoLevel;          // read per block: applies live
        AudioLevelShaper.HumSuppressionEnabled = _settings.AudioHumSuppression;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Ses seviyesi", ex); }
        DiagnosticsService.Info($"Audio level shaping: auto level {_settings.AudioAutoLevel}, hum suppression {_settings.AudioHumSuppression}.");
    }

    private void XboxBassTreble_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.XboxBassTreble = XboxBassTreble.IsChecked == true;
        ApplyPadModeOptions(_padMode);
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Xbox bas/tiz", ex); }
        DiagnosticsService.Info("Xbox bass/treble split: " + _settings.XboxBassTreble + " (effective " + XboxBodyEnvelope.Preferred + ").");
    }

    private void XboxSensa_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.XboxSensa = XboxSensaOutput.IsChecked == true;
        XboxSensa.Enabled = _settings.XboxSensa;   // the engine drops or picks up the GIP route within ~1 s
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Xbox modu Sensa", ex); }
        ApplyPadModeOptions(_padMode);
        // Audio mode picks its output (palms or body) when it starts: restart it so the new choice applies.
        if (ModeAudio.IsChecked == true && _run == RunState.Audio) { _orch.StopOutputs(); _orch.ResetRetry(); _ = EnsureModeRunningAsync(); }
        DiagnosticsService.Info("Xbox-mode Sensa (palms + trigger motors over GIP): " + _settings.XboxSensa + ".");
    }

    private void BigMotorCeiling_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.BigMotorCeiling = BigMotorCeiling.IsChecked == true;
        BodyMotorLimit.Enabled = _settings.BigMotorCeiling;   // read at every motor write: applies live
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Büyük motor sınırı", ex); }
        DiagnosticsService.Info("Big body motor ceiling: " + _settings.BigMotorCeiling + ".");
    }

    private void PalmTriggerFeel_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.PalmTriggerFeel = PalmTriggerFeel.IsChecked == true;
        _bridge.PalmTriggerFeel = _settings.PalmTriggerFeel;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Avuç tetik hissi", ex); }
        DiagnosticsService.Info("Trigger effects in PC mode: " + (_settings.PalmTriggerFeel ? "palms (Sensa)" : "body motors") + ".");
    }

    private void TriggerFeel_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        _settings.TriggerFeel = int.Parse(tag);
        _bridge.TriggerMotors = _settings.TriggerFeel == 1;
        RouteImpulse.IsChecked = _bridge.TriggerMotors;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Trigger feel", ex); }
        DiagnosticsService.Info("Trigger effects in Xbox mode: " + (_settings.TriggerFeel == 1 ? "trigger motors + haptics" : "classic (body motors)") + ".");
    }

    private void ApplyXboxMix()
    {
        _settings.XboxNativeMix = new((int)XboxPcmLevel.Value, (int)XboxRumbleLevel.Value,
            (int)XboxTriggerLevel.Value, (XboxBodyPriority)XboxBodyMixPriority.SelectedIndex,
            RoutePcm.IsChecked == true, RouteRumble.IsChecked == true, RouteTriggers.IsChecked == true);
        _bridge.XboxMix = _settings.XboxNativeMix;
    }

    private void RouteImpulse_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        (RouteImpulse.IsChecked == true ? TriggerFeelMotors : TriggerFeelClassic).IsChecked = true;
        RefreshStatus();
    }

    private bool _updatingGameFeedbackPreset;

    private void GameFeedbackPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingGameFeedbackPreset ||
            GameFeedbackPreset.SelectedItem is not ComboBoxItem { Tag: string preset } || preset == "Custom") return;

        _updatingGameFeedbackPreset = true;
        try
        {
            RoutePcm.IsChecked = preset == "Full";
            RouteRumble.IsChecked = true;
            RouteTriggers.IsChecked = preset != "Rumble";
            if (preset != "Rumble") RouteImpulse.IsChecked = true;
        }
        finally { _updatingGameFeedbackPreset = false; }

        RouteSource_Changed(sender, e);
    }

    private void UpdateGameFeedbackPreset()
    {
        if (_updatingGameFeedbackPreset) return;
        var mix = _settings.XboxNativeMix;
        int index = (mix.PcmEnabled, mix.RumbleEnabled, mix.TriggersEnabled, _bridge.TriggerMotors) switch
        {
            (false, true, true, true) => 0,
            (true, true, true, true) => 1,
            (false, true, false, _) => 2,
            _ => 3
        };
        _updatingGameFeedbackPreset = true;
        try { GameFeedbackPreset.SelectedIndex = index; }
        finally { _updatingGameFeedbackPreset = false; }
        GameFeedbackPresetHint.Text = index switch
        {
            0 => "Normal titreşim gövdede kalır. Wolverine Xbox modunda L2/R2 efektleri, tetiği çekme miktarına göre tetik içinde titreşimle taklit edilir.",
            1 => "HD haptik deneysel olarak gövdeye çevrilir ve normal titreşim aktarılır. Wolverine Xbox modunda L2/R2 efektleri çekişe göre tetik içinde titreşim üretir.",
            2 => "Oyunun sanal DualSense'e gönderdiği standart titreşim gövdeye aktarılır.",
            _ => "Oyunun desteklediği kaynakları aşağıdan seç. Tetik içi çıkış için Wolverine Xbox modunda olmalı."
        };
    }

    private void XboxMix_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplyXboxMix();
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Xbox Native mix", ex); }
    }

    private void AudioProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out AudioHapticProfile profile)) return;
        _settings.AudioProfile = profile;
        AudioProfileHint.Text = profile switch
        {
            AudioHapticProfile.Controlled => "30–130 Hz · %70. Derin bas odaklı. Konuşma ağırlıklı veya rekabetçi oyunlarda dene.",
            AudioHapticProfile.Balanced => "30–150 Hz · %70. Biraz daha geniş bas tepkisi. Hikâyeli oyunlar ve genel kullanım için.",
            AudioHapticProfile.Dynamic => "30–100 Hz · %30 + 100–200 Hz · %10. İki bantlı, daha hafif karışım. Müzik ve aksiyon sahnelerinde dene.",
            _ => "Ek profil filtresi kapalı. Mevcut ses işleme davranışını kullanır."
        };
        if (!_initialized) return;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Ses profili", ex); }
        if (SelectedMode != HapticMode.Audio) return;
        if (_run == RunState.Audio && !_busy) StopAll();
        _nextAutoStartTick = 0;
        _ = EnsureModeRunningAsync();
    }

    private void RouteSource_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingGameFeedbackPreset) return;
        ApplyXboxMix();
        _settings.AudioOutputEnabled = RouteAudio.IsChecked == true;
        if (SelectedMode == HapticMode.Audio || (SelectedMode == HapticMode.Native && !_bridge.IsVirtualDualSenseRunning))
        {
            if (_run != RunState.Off) StopAll();
            _nextAutoStartTick = 0;
            _ = EnsureModeRunningAsync();
        }
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Titreşim kaynakları", ex); }
        RefreshStatus();
    }

    private async void PadModeXbox_Click(object sender, RoutedEventArgs e)
    {
        if (_padModeBusy) return;
        _padModeBusy = true;
        PadModeXboxBtn.IsEnabled = false;
        PadModeText.Text = "Xbox moduna geçiliyor (kol bir an bağlantıyı kesip yeniden bağlanır)…";
        string note;
        try
        {
            var (ok, msg) = await WolverineModeControl.SwitchToXboxAsync();
            note = msg;
            DiagnosticsService.Info("Controller mode switch to Xbox: " + (ok ? "ok" : "failed") + " - " + msg);
        }
        catch (Exception ex) { NoteError("Mode switch", ex); note = "Hata: " + ex.Message; }
        _padModeBusy = false;
        await RefreshPadModeAsync(note);
    }

    // HidHide: hides the physical Xbox pad from games while the virtual DualSense runs.
    private async Task RefreshHidHideAsync()
    {
        var h = await Task.Run(HidHideControl.Query);
        bool running = _bridge.IsVirtualDualSenseRunning;
        // Left over from a crash: undo it now when we can (elevated), else offer the button.
        if (h.RestorePending && !running && VirtualDualSenseBridge.IsElevated)
        {
            try { DiagnosticsService.Info("HidHide: " + await Task.Run(HidHideControl.Restore)); } catch (Exception ex) { NoteError("HidHide restore", ex); }
            h = await Task.Run(HidHideControl.Query);
        }
        HhText.Text = !h.Installed ? "HidHide: ✗ kurulu değil — oyun Xbox kolunu da görür (çift giriş)."
            : "HidHide " + h.Version + ": " + (h.Ready ? "✓ kurulu" : "✗ eksik (" + (h.CliPath == null ? "CLI yok" : "filtre yok") + ")") +
              " • Xbox kolu " + (h.PadNodes == 0 ? "bulunamadı" : h.PadNodesHidden == h.PadNodes && h.CloakOn == true ? "oyundan gizli" : "oyuna görünür") +
              (h.Error != null ? " — " + h.Error : "");
        HhInstallBtn.Visibility = h.Installed ? Visibility.Collapsed : Visibility.Visible;
        HhRestoreBtn.Visibility = h.RestorePending && !running ? Visibility.Visible : Visibility.Collapsed;
        HhHideCheckbox.IsEnabled = h.Ready;
    }

    private void HhHide_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.HidePadInGames = HhHideCheckbox.IsChecked == true;
        _bridge.HidePadInGames = _settings.HidePadInGames;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("HidHide setting", ex); }
        DiagnosticsService.Info("Hide Xbox pad in games while bridging: " + _settings.HidePadInGames + " (applies on next bridge start).");
    }

    private void HhInstall_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(HidHideControl.DownloadUrl) { UseShellExecute = true }); }
        catch (Exception ex) { NoteError("HidHide download page", ex); }
        HhText.Text = "Resmî HidHide sayfası açıldı. Kurulumdan sonra Windows'u yeniden başlat.";
    }

    private async void HhRestore_Click(object sender, RoutedEventArgs e)
    {
        HhRestoreBtn.IsEnabled = false;
        try
        {
            string msg = VirtualDualSenseBridge.IsElevated ? HidHideControl.Restore()
                : (await VirtualControllerSetup.RunElevatedAsync(VirtualControllerSetup.HidHideRestoreArg)).Message;
            DiagnosticsService.Info("HidHide: " + msg);
        }
        catch (Exception ex) { NoteError("HidHide restore", ex); }
        HhRestoreBtn.IsEnabled = true;
        await RefreshHidHideAsync();
    }

    private async Task RunVirtualControllerStepAsync(string arg, string working)
    {
        if (_vcBusy) return;
        _vcBusy = true;
        VcInstallBtn.IsEnabled = VcTestBtn.IsEnabled = VcUninstallBtn.IsEnabled = false;
        VcSelfTestText.Text = working + " (Windows yönetici izni isteyecek)…";
        string result;
        try { var (_, msg) = await VirtualControllerSetup.RunElevatedAsync(arg); result = msg; }
        catch (Exception ex) { NoteError("HIDMaestro " + arg, ex); result = "Hata: " + ex.Message; }
        _vcBusy = false;
        await RefreshVirtualControllerAsync(result);
    }

    private async void VcInstall_Click(object sender, RoutedEventArgs e)
    {
        await RunVirtualControllerStepAsync(VirtualControllerSetup.InstallArg, "Kuruluyor");
        if (VirtualControllerSetup.Query().Ready)
            await RunVirtualControllerStepAsync(VirtualControllerSetup.SelfTestArg, "Kurulum tamam, sağlık testi yapılıyor");
    }

    private async void VcBridge_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge.IsVirtualDualSenseRunning)
        {
            _bridge.StopVirtualDualSense();
            _bridge.Stop();
            _run = RunState.Off;
            _nextAutoStartTick = long.MaxValue;   // stopped by the user: no automatic restart until the mode is re-selected
            UpdateBridgeStatus();
            return;
        }
        if (!VirtualDualSenseBridge.IsElevated)
        {
            if (!RestartElevated(App.StartVirtualDualSenseArg))
                VcBridgeText.Text = "Yönetici izni verilmedi.";
            return;
        }
        await StartVirtualBridgeAsync();
    }

    private bool RestartElevated(string argument)
    {
        if (argument == App.StartVirtualDualSenseArg && ElevatedLaunchTask.TryRun())
        {
            DiagnosticsService.Info("Virtual bridge started through the elevated task (no UAC prompt).");
            ExitCompletely();
            return true;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process == null) return false;
            ExitCompletely();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != 1223) NoteError("Yönetici olarak başlatma", ex);
            return false;
        }
    }

    private async Task StartVirtualBridgeAsync()
    {
        if (_busy || _starting) return;
        _busy = true;
        try
        {
            StopAll();
            ModeNative.IsChecked = true; // trigger conversion must never run under the Audio/Normal label
            int slot = _settings.PreferredXInputSlot is >= 0 and <= 3 ? _settings.PreferredXInputSlot : -1;
            bool ok = await _bridge.StartVirtualDualSenseAsync(slot);
            if (ok) _run = RunState.Native;
        }
        catch (Exception ex) { NoteError("Virtual DualSense", ex); }
        finally { _busy = false; UpdateBridgeStatus(); }
    }

    private void UpdateBridgeStatus()
    {
        if (VcBridgeText == null) return;
        bool running = _bridge.IsVirtualDualSenseRunning;
        var (h, o, t) = _bridge.VirtualDualSenseCounters;
        VcBridgeText.Text = _bridge.VirtualDualSenseStatus + (running ? $" • titreşim blokları {h}, rapor {o}, tetik {t}\n" + _bridge.VirtualDualSenseInputCheck +
            (_bridge.VirtualDualSenseHidHideNote.Length > 0 ? "\n" + _bridge.VirtualDualSenseHidHideNote : "") : "");
    }

    private async void VcSelfTest_Click(object sender, RoutedEventArgs e) =>
        await RunVirtualControllerStepAsync(VirtualControllerSetup.SelfTestArg, "Sanal DualSense oluşturulup kontrol ediliyor");

    private async void VcUninstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            "HIDMaestro sürücüsü, sertifikası ve USB/IP arka ucu kaldırılsın mı?" + Environment.NewLine + "PadForge gibi başka bir program da HIDMaestro kullanıyorsa o da çalışmaz.",
            "Sanal DualSense'i kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await RunVirtualControllerStepAsync(VirtualControllerSetup.UninstallArg, "Kaldırılıyor");
    }

    // ───────────────────────── Genel: connection checklist ─────────────────────────

    private async void RefreshChecks_Click(object sender, RoutedEventArgs e) => await RefreshChecksAsync();

    private sealed record CheckResult(WolverineControllerMode Mode, int SensaPaths, bool Synapse, VirtualControllerStatus Virtual, string? DualSenseEndpoint);

    private async Task RefreshChecksAsync()
    {
        CheckResult r = await Task.Run(() =>
        {
            var mode = WolverineModeControl.Detect();
            int sensa = mode == WolverineControllerMode.Pc ? SensaHidTransport.Candidates().Count : 0;
            bool synapse = SynapseSensaCheck.IsSynapseRunning();
            var virtualStatus = VirtualControllerSetup.Query();
            string? endpoint = null;
            try { endpoint = NativeBridgeService.FindDualSenseEndpoint()?.Name; }
            catch (Exception ex) { DiagnosticsService.Error("Endpoint check", ex); }
            return new CheckResult(mode, sensa, synapse, virtualStatus, endpoint);
        });

        ApplyPadModeOptions(r.Mode);
        // PC mode works over the cable AND the HyperSpeed dongle (both 1532:0A4C); Xbox mode is a valid mode too.
        switch (r.Mode)
        {
            case WolverineControllerMode.Pc: SetState(DotWolverine, HintWolverine, GoodBrush, "PC modunda: avuçta Sensa HD titreşim."); break;
            case WolverineControllerMode.Xbox: SetState(DotWolverine, HintWolverine, GoodBrush, XboxSensa.Enabled
                ? "Xbox modunda: avuçta Sensa (GIP) + tetik motorları."
                : "Xbox modunda: tetik motorları var; Sensa ayarı kapalı. " + WolverineModeControl.PcComboHint); break;
            default: SetState(DotWolverine, HintWolverine, BadBrush, "Bulunamadı. Kolu kablo ya da kendi alıcısıyla bağla ve aç."); break;
        }
        if (r.Mode == WolverineControllerMode.Pc)
            SetState(DotSynapse, HintSynapse, r.SensaPaths > 0 ? GoodBrush : BadBrush, r.SensaPaths > 0
                ? "Hazır: titreşim doğrudan kola yazılır, Synapse gerekmez." + (r.Synapse ? " Synapse açık: 'Audio to Haptics' kapalı olmalı." : "")
                : "Kolun Sensa arabirimi açılamadı. Kolu çıkarıp tak.");
        else SetState(DotSynapse, HintSynapse, MutedBrush, r.Mode == WolverineControllerMode.Xbox ? "Xbox modunda kullanılmaz." : "Kol bağlı değil.");
        SetState(DotSdk, HintSdk, r.Virtual.Ready ? GoodBrush : WarnBrush, r.Virtual.Ready
            ? "Kurulu. 'Oyunun DualSense titreşimi' modu için gerekli."
            : "Kurulu değil. Yalnız 'Oyunun DualSense titreşimi' için gerekir: Ayarlar → Sanal DualSense → Kur.");
        SetState(DotDualSense, HintDualSense, r.DualSenseEndpoint != null ? GoodBrush : MutedBrush, r.DualSenseEndpoint != null
            ? "4 kanal ses aygıtı: " + r.DualSenseEndpoint
            : "Şu an yok: sanal köprü kapalı ve USB DualSense takılı değil. 'Oyunun DualSense titreşimi' modu köprüyü açar.");
    }

    private static void SetState(System.Windows.Shapes.Ellipse dot, TextBlock hint, Brush brush, string text)
    {
        dot.Fill = brush;
        hint.Text = text;
    }

    private static void SetCheck(System.Windows.Shapes.Ellipse dot, TextBlock hint, bool ok, string okText, string badText)
    {
        dot.Fill = ok ? GoodBrush : BadBrush;
        hint.Text = ok ? okText : badText;
    }

    // ───────────────────────── Genel: mode (auto-run) ─────────────────────────
    // Choosing a mode runs it immediately, and the saved mode runs at every app start.
    // If a source/runtime is missing, the timer retries quietly every few seconds.

    private HapticMode SelectedMode =>
        ModeAudio.IsChecked == true ? HapticMode.Audio :
        ModeNative.IsChecked == true ? HapticMode.Native : HapticMode.Normal;

    private bool _starting => _orch.Starting;
    private WolverineMode _audioRoute => _orch.AudioRoute;
    private AudioHapticsBridge? _xboxAudio => _orch.XboxAudio;
    private XboxAudioGameBridge? _combinedXbox => _orch.CombinedXbox;
    private long _nextAutoStartTick { get => _orch.NextAutoStartTick; set => _orch.NextAutoStartTick = value; }
    private string? _nativeWaitNote => _orch.NativeWaitNote;
    private bool UseXboxGameBridge => _orch.UseXboxGameBridge;

    private void CombineXboxFeedback_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not System.Windows.Controls.CheckBox option) return;
        if (_orch.UpdateCombinedOptions()) return;
        bool enablingFirst = option.IsChecked == true &&
            (IncludeXboxGameRumble.IsChecked != true || IncludeXboxGameTriggers.IsChecked != true);
        if (enablingFirst && !VirtualDualSenseBridge.IsElevated)
        {
            string argument = App.StartXboxAudioBridgeArg;
            if (option == IncludeXboxGameTriggers) argument += " --xbox-triggers-only";
            if (!RestartElevated(argument)) option.IsChecked = false;
            return;
        }
        if (enablingFirst &&
            System.Windows.MessageBox.Show(
                "RW3 sanal Xbox kolu oluşturacak ve fiziksel Wolverine'i HidHide ile geçici gizleyecek. " +
                "Mevcut HidHide gizleme listesi etkinleşebilir. Oyunu yeniden başlat. " +
                "İki Xbox seçeneğini de kapatmak veya RW3'ten çıkmak değişiklikleri geri alır.",
                "Xbox geri bildirimi", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            option.IsChecked = false;
            return;
        }
        if (SelectedMode != HapticMode.Audio || _busy) return;
        if (_run == RunState.Audio) StopAll();
        _orch.ResetRetry();
        _ = EnsureModeRunningAsync();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        var mode = SelectedMode;
        if (_initialized && _run != RunState.Off) StopAll();
        _settings.Mode = mode;
        _settings.Route = HapticRouteCatalog.DefaultForMode(mode);
        _orch.ResetRetry();
        _orch.UserPickedNative = _initialized && mode == HapticMode.Native;
        if (_initialized)
        {
            try { _store.Save(_settings); } catch (Exception ex) { NoteError("Settings save", ex); }
            DiagnosticsService.Info("Selected mode: " + mode + " (auto-run).");
        }
        if (_normalMenu != null) _normalMenu.Checked = mode == HapticMode.Normal;
        if (_audioMenu != null) _audioMenu.Checked = mode == HapticMode.Audio;
        if (_nativeMenu != null) _nativeMenu.Checked = mode == HapticMode.Native;
        RefreshStatus();
        if (_initialized) _ = EnsureModeRunningAsync();
    }

    private void CycleMode()
    {
        if (ModeNormal.IsChecked == true) ModeAudio.IsChecked = true;
        else if (ModeAudio.IsChecked == true) ModeNative.IsChecked = true;
        else ModeNormal.IsChecked = true;
    }

    /// <summary>Starts the selected mode if it is not running (no-op for Normal or during tests).</summary>
    private Task EnsureModeRunningAsync() => _orch.EnsureRunningAsync();

    private void StopAll()
    {
        _testCancel?.Cancel();
        _orch.StopOutputs();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _tickCount++;
            _orch.Tick(_performance, _tickCount);   // CPU guard, liveness watchdog, auto-run
            // The mode can change by key combo at any time; recheck every 3 s while audio mode is shown.
            if (_tickCount % 12 == 0 && SelectedMode == HapticMode.Audio && !_padModeBusy) _ = RefreshPadOptionsAsync();
            if (TabSettings.IsChecked == true && _bridge.IsVirtualDualSenseRunning) UpdateBridgeStatus();
            RefreshStatus();
        }
        catch (Exception ex) { NoteError("Status refresh", ex); }
    }

    private void RefreshStatus()
    {
        var mode = SelectedMode;
        UpdateGameFeedbackPreset();
        NativeSourceRows.Visibility = mode == HapticMode.Native ? Visibility.Visible : Visibility.Collapsed;
        AudioSourceRows.Visibility = mode == HapticMode.Audio ? Visibility.Visible : Visibility.Collapsed;
        NormalSourceRows.Visibility = mode == HapticMode.Normal ? Visibility.Visible : Visibility.Collapsed;
        RouteNativeHint.Text = !_bridge.IsVirtualDualSenseRunning
            ? "Oyunu açmadan önce sanal DualSense köprüsünü başlat. PCM tek başına ses köprüsünden de alınabilir."
            : _bridge.VirtualDualSenseMode == WolverineControllerMode.Xbox && _bridge.TriggerMotors
                ? "Açık gövde kaynakları gövdeye, açıksa L2/R2 efektleri bağımsız tetik motorlarına gider."
                : "Açık kaynaklar gövdeye yönlendirilir.";
        RouteVirtualButton.Content = _bridge.IsVirtualDualSenseRunning ? "Sanal köprüyü durdur" : "Sanal köprüyü başlat";
        RouteVirtualButton.IsEnabled = !_busy && !_starting;
        RouteImpulse.IsEnabled = RouteTriggers.IsChecked == true;
        bool running = _run != RunState.Off;
        LevelPanel.Visibility = _run == RunState.Native && _settings.XboxNativeMix.PcmEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        string waitText = _busy ? "Test sürüyor; bitince otomatik devam eder."
            : _lastErrorNoted != null ? "Bekleniyor: " + _lastErrorNoted +
                (_nextAutoStartTick == long.MaxValue ? " Seçeneği kapatıp yeniden açarak dene." : " (birkaç saniyede bir yeniden denenir)")
            : "Başlatılıyor…";

        if (!running && !_busy && (mode == HapticMode.Audio && !_settings.AudioOutputEnabled ||
            mode == HapticMode.Native && !_settings.XboxNativeMix.PcmEnabled))
            waitText = mode == HapticMode.Native && (_settings.XboxNativeMix.RumbleEnabled || _settings.XboxNativeMix.TriggersEnabled)
                ? "Oyun geri bildirimi için sanal DualSense köprüsünü başlat."
                : "Kaynak kapalı. Titreşim tablosundan yeniden açabilirsin.";

        switch (mode)
        {
            case HapticMode.Normal:
                StatusTitle.Text = "Normal mod etkin";
                StatusDetail.Text = "Oyunun titreşimi doğrudan kola gider. Bu uygulamanın bir şey çalıştırmasına gerek yok.";
                ModeNote.Text = "Razer'ın kendi Sensa HD efektleri için Synapse'te 'Sensa HD Games' kaynağını kullan.";
                SetBadge(GoodBrush, "Normal");
                break;
            case HapticMode.Audio:
                var snap = _rumble.Snapshot();
                string route = _audioRoute switch
                {
                    WolverineMode.Pc => "PC modu → Sensa motorları (sol/sağ, v2)",
                    WolverineMode.Xbox => _combinedXbox != null ? $"Sanal Xbox · oyun geri bildirim raporu: {_combinedXbox.FeedbackReports}"
                        : _xboxAudio != null ? "Xbox modu → gövde motorları" : "Xbox modu → Sensa avuçlar (GIP, sol/sağ)",
                    _ => "Klasik Xbox titreşimi"
                };
                bool sound = _audioRoute switch
                {
                    WolverineMode.Pc => _bridge.AudioHasRecentSound,
                    WolverineMode.Xbox => _combinedXbox?.HasRecentSound ?? _xboxAudio?.HasRecentSound ?? _bridge.AudioHasRecentSound,
                    _ => _xboxAudio?.HasRecentSound ?? (snap.LastActiveSoundTick > 0 && Environment.TickCount64 - snap.LastActiveSoundTick < 3000)
                };
                StatusTitle.Text = running ? "Sesten titreşim çalışıyor" : "Sesten titreşim";
                StatusDetail.Text = running
                    ? route + " • " + (sound ? "ses algılanıyor." : "şu an ses yok veya çok sessiz. Ses kaydedilmez.")
                    : waitText;
                ModeNote.Text = "Ses yalnız avuç/gövde titreşimi üretir: PC modunda Sensa, Xbox modunda da Sensa ('Xbox modunda Sensa' kapalıysa ana motorlar). Tetiklere ses komutu gönderilmez. " +
                                "Synapse'te 'Audio to Haptics' açıksa kapat; yoksa iki sistem aynı anda titretir.";
                SetBadge(running ? GoodBrush : WarnBrush, running ? "Çalışıyor" : "Bekliyor");
                break;
            default:
                if (_bridge.IsVirtualDualSenseRunning && !_settings.XboxNativeMix.PcmEnabled)
                {
                    var (_, reports, triggers) = _bridge.VirtualDualSenseCounters;
                    StatusTitle.Text = "Sanal DualSense hazır";
                    StatusDetail.Text = $"PCM kapalı · Geri bildirim raporu: {reports} · Tetik raporu: {triggers}";
                    SetBadge(GoodBrush, "Köprü açık");
                }
                else if (_bridge.IsVirtualDualSenseRunning)
                {
                    var (blocks, reports, triggers) = _bridge.VirtualDualSenseCounters;
                    bool xbox = _bridge.VirtualDualSenseMode == WolverineControllerMode.Xbox;
                    StatusTitle.Text = blocks > 0 ? "Oyunun DualSense titreşimi çalışıyor" : "Sanal DualSense hazır · oyun bekleniyor";
                    StatusDetail.Text = (xbox ? "Xbox modu: haptik → gövde, L2/R2 → tetik motorları" : "PC modu: haptik → Sensa (avuç)") +
                        $" • haptik blok {blocks}, tetik raporu {triggers}" +
                        (_bridge.VirtualDualSenseInputCheck.Length > 0 ? " • " + _bridge.VirtualDualSenseInputCheck : "");
                    SetBadge(GoodBrush, "Köprü açık");
                }
                else if (running)
                {
                    bool signal = _bridge.HasRecentHapticSignal;
                    StatusTitle.Text = signal ? "Arka kanal PCM gözlendi" : "Native v2 açık · sinyal bekleniyor";
                    StatusDetail.Text = signal
                        ? NativeCaptureHealth.Describe(NativeCaptureState.RearChannelEnergyObserved) + " • v2 motor " +
                          _bridge.EngineTickHz.ToString("F0") + " kare/sn."
                        : "Kaynak: " + (_bridge.SourceName ?? "?") + ". DualSense haptiği destekleyen oyunu aç.";
                    LeftLevel.Value = Math.Clamp(_bridge.LeftRms * 4, 0, 1);
                    RightLevel.Value = Math.Clamp(_bridge.RightRms * 4, 0, 1);
                    SetBadge(WarnBrush, signal ? "Deneysel PCM" : "Hazır");
                }
                else
                {
                    StatusTitle.Text = "Oyunun DualSense titreşimi";
                    StatusDetail.Text = _nativeWaitNote != null && _lastErrorNoted == null && !_busy ? _nativeWaitNote : waitText;
                    SetBadge(WarnBrush, "Bekliyor");
                }
                ModeNote.Text = _settings.XboxNativeMix.PcmEnabled
                    ? "USB DualSense takılıysa onun sesi, değilse sanal DualSense köprüsü kullanılır. HD haptik 33 ms karelerle Sensa'ya (PC modu) ya da gövde motorlarına (Xbox modu) çevrilir."
                    : "";
                break;
        }

        ResourceText.Text = "Uygulama CPU: %" + _performance.CpuPercent.ToString("F2") +
                            " · RAM: " + _performance.MemoryMb.ToString("F0") + " MB";
        DiagnosticsText.Text = _lastErrorNoted != null ? "Son sorun: " + _lastErrorNoted : "Son hata: yok";
    }

    private void SetBadge(Brush dot, string text)
    {
        RunBadgeDot.Fill = dot;
        RunBadgeText.Text = text;
    }

    // ───────────────────────── Test: Sensa sides ─────────────────────────

    private void SensaLeft_Click(object sender, RoutedEventArgs e) => _ = RunSensaTestAsync(HapticTestChannel.Left);
    private void SensaRight_Click(object sender, RoutedEventArgs e) => _ = RunSensaTestAsync(HapticTestChannel.Right);
    private void SensaBoth_Click(object sender, RoutedEventArgs e) => _ = RunSensaTestAsync(null);

    /// <summary>Like <see cref="BeginTest"/> but leaves the running mode alone (demo through the virtual bridge).</summary>
    private CancellationToken BeginTestKeepingOutputs()
    {
        _testCancel?.Cancel();
        _testCancel = new CancellationTokenSource();
        SetTestButtons(false);
        return _testCancel.Token;
    }

    private CancellationToken BeginTest()
    {
        if (_run != RunState.Off) StopAll();
        _testCancel?.Cancel();
        _testCancel = new CancellationTokenSource();
        SetTestButtons(false);
        return _testCancel.Token;
    }

    private void SetTestButtons(bool enabled)
    {
        SensaLeftBtn.IsEnabled = SensaRightBtn.IsEnabled = SensaBothBtn.IsEnabled = AudioPanBtn.IsEnabled =
            AudioPingPongBtn.IsEnabled = AudioDir8Btn.IsEnabled = AudioFrontBackBtn.IsEnabled = enabled;
        DemoPlayBtn.IsEnabled = DemoSceneCombo.IsEnabled = enabled;
        ChannelReplayBtn.IsEnabled = ChannelReferenceBtn.IsEnabled = ChannelEndpointBtn.IsEnabled = ChannelRefreshEndpointsBtn.IsEnabled = ChannelEndpointCombo.IsEnabled = enabled;
        ChannelSceneCombo.IsEnabled = ChannelMicCombo.IsEnabled = ChannelMicEnabled.IsEnabled = enabled;
        ChannelEndpointLevelEnabled.IsEnabled = enabled;
        SensaResetBtn.IsEnabled = enabled;
        XboxCombinedTestBtn.IsEnabled = enabled;
        XboxTriggerBackendBtn.IsEnabled = enabled;
        CalibBtn.IsEnabled = enabled;
    }

    private string? _channelReportPath;
    private void ChannelReplay_Click(object sender, RoutedEventArgs e) => _ = RunChannelLabAsync(false);
    private void ChannelReference_Click(object sender, RoutedEventArgs e) => _ = RunChannelLabAsync(true);
    private void ChannelEndpoint_Click(object sender, RoutedEventArgs e) => _ = RunChannelLabAsync(false, true);
    private async void ChannelRefreshEndpoints_Click(object sender, RoutedEventArgs e)
    {
        try { await RefreshChannelEndpointsAsync(); }
        catch (Exception ex) { ChannelStatus.Text = "Uç nokta listesi okunamadı: " + ex.Message; }
    }
    private async Task RefreshChannelEndpointsAsync()
    {
        var endpoints = await Task.Run(NativeDualSenseCapture.EnumeratePossibleSources);
        var compatible = new List<NativeDualSenseCapture.EndpointCandidate>();
        foreach (var endpoint in endpoints) if (endpoint.CanCapture) compatible.Add(endpoint);
        ChannelEndpointCombo.ItemsSource = compatible;
        ChannelEndpointCombo.SelectedIndex = compatible.Count == 1 ? 0 : -1;
    }
    private void ChannelStop_Click(object sender, RoutedEventArgs e)
    {
        _testCancel?.Cancel();
        _bridge.Stop();
    }
    private void ChannelReport_Click(object sender, RoutedEventArgs e)
    {
        if (_channelReportPath != null) OpenPath(_channelReportPath);
    }

    private async Task RunChannelLabAsync(bool reference, bool endpointRoute = false)
    {
        if (_busy || _starting || ChannelSceneCombo.SelectedItem is not ChannelLabScene scene) return;
        var chosenEndpoint = ChannelEndpointCombo.SelectedItem as NativeDualSenseCapture.EndpointCandidate;
        if (endpointRoute && chosenEndpoint == null)
        {
            ChannelStatus.Text = "4ch/48k uç noktasını yenile ve seç. Doğrudan PCM tekrarına geri dönülmedi.";
            return;
        }
        string? micId = null;
        if (!reference && ChannelMicEnabled.IsChecked == true)
        {
            if (ChannelMicCombo.SelectedItem is not LabMicrophone microphone)
            {
                ChannelStatus.Text = "Ölçüm için bir mikrofon seç; yoksa mikrofon ölçümünü kapat.";
                return;
            }
            micId = microphone.Id;
        }
        _busy = true;
        var token = BeginTest();
        _bridge.Stop(); // No other HAR owner may run during this independent lab operation.
        ChannelStatus.Text = "Hazırlanıyor • " + scene.Title;
        ChannelReportBtn.IsEnabled = false;
        _channelReportPath = null;
        try
        {
            if (endpointRoute)
            {
                var progress = new Progress<string>(message => ChannelStatus.Text = scene.Title + " • " + message);
                var result = await EndpointLabRunner.RunAsync(scene, chosenEndpoint!.Id, _settings.OutputLevelPercent,
                    _settings.NativeGainPercent, micId, token, progress, ChannelEndpointLevelEnabled.IsChecked == true);
                string folder = Path.Combine(Path.GetDirectoryName(DiagnosticsService.LogFile)!, "channel-lab");
                Directory.CreateDirectory(folder);
                _channelReportPath = Path.Combine(folder, "endpoint-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(_channelReportPath, System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                ChannelReportBtn.IsEnabled = true;
                ChannelStatus.Text = "Gerçek WASAPI: " + result.LiveCapturedFrames + " çerçeve • L/R etkin blok " +
                    result.LiveLeftActiveBlocks + "/" + result.LiveRightActiveBlocks + " • örnekler birebir: " +
                    (result.SourceComparison.ExactActiveSamples ? "evet" : "hayır") + " • gözlenen kazanç " +
                    result.SourceComparison.ObservedGain.ToString("F3") + ". Sentetik kaynak; gerçek oyun/fiziksel HD doğrulanmadı.";
            }
            else if (reference)
            {
                var pcm = DualSenseChannelFixtures.Build(scene);
                string hash = DualSenseChannelFixtures.Sha256(pcm);
                var endpoint = await Task.Run(NativeBridgeService.FindDualSenseEndpoint) ??
                    throw new InvalidOperationException("DualSense 4ch/48k aygıtı yok; referans çalınmadı.");
                using var awakeStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var awake = Task.Run(() => DualSenseHidControl.KeepAwake(awakeStop.Token));
                try
                {
                    await Task.Delay(150, token);
                    await DualSensePatternPlayer.PlayPcm16Async(endpoint.Id, pcm, token);
                }
                finally { awakeStop.Cancel(); await awake; }
                ChannelStatus.Text = "DualSense Exclusive PCM gönderimi bitti. SHA256 " + hash[..16] +
                    "; fiziksel yanıt doğrulanmadı. Wolverine bu referans testinde çalıştırılmadı.";
            }
            else
            {
                var progress = new Progress<string>(message => ChannelStatus.Text = scene.Title + " • " + message);
                var result = await ChannelLabRunner.ReplayAsync(scene, _settings.OutputLevelPercent,
                    _settings.NativeGainPercent, micId, token, progress);
                _channelReportPath = ChannelLabRunner.SaveReport(result);
                ChannelReportBtn.IsEnabled = true;
                ChannelStatus.Text = "PCM değişmedi: " + (result.SourceSha256 == result.SourceAfterSha256 ? "evet" : "HAYIR") +
                    " • motor " + result.EngineHz.ToString("F0") + " Hz • " + result.SourceFrames + " kaynak çerçevesi" +
                    (result.Active == null ? " • mikrofon ölçülmedi." :
                        " • mikrofon RMS: " + result.Background!.BandRms.ToString("G3") + " → " +
                        result.Active.BandRms.ToString("G3") + " → " + result.Tail!.BandRms.ToString("G3")) +
                    " • USB kaynak / gerçek oyun / fiziksel HD doğrulanmadı.";
            }
        }
        catch (OperationCanceledException) { ChannelStatus.Text = "Durduruldu; çıkış kapatıldı. Tamamlanmış ölçüm raporu yok."; }
        catch (Exception ex) { NoteError("DualSense channel lab", ex); ChannelStatus.Text = "Test tamamlanmadı: " + ex.Message; }
        finally { _busy = false; SetTestButtons(true); }
    }

    // ───────────────────────── game signal simulator (virtual DualSense) ─────────────────────────

    private CancellationTokenSource? _gameSimCancel;

    private void GameSimCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GameSimCombo.SelectedItem is GameSimScene s && GameSimStatus != null && _gameSimCancel == null)
            GameSimStatus.Text = s.Hint;
    }

    private void GameSimStop_Click(object sender, RoutedEventArgs e) => _gameSimCancel?.Cancel();

    private async void GameSim_Click(object sender, RoutedEventArgs e)
    {
        if (_gameSimCancel != null || GameSimCombo.SelectedItem is not GameSimScene scene) return;
        if (!_bridge.IsVirtualDualSenseRunning)
        {
            GameSimStatus.Text = "Önce Ayarlar → Sanal DualSense → köprüyü başlat. Simülasyon sanal DualSense'e, oyunun yolladığı gibi yollar.";
            return;
        }
        if ((scene.Id is "combined" or "all") &&
            (_bridge.VirtualDualSenseMode != WolverineControllerMode.Xbox || !_bridge.TriggerMotors))
        {
            GameSimStatus.Text = "Birleşik test için Wolverine Xbox modunda ve Ayarlar → DualSense tetik efektleri → Haptik + tetik motorları açık olmalı. Ayarlar otomatik değiştirilmedi.";
            return;
        }
        if (_bridge.VirtualDualSenseHidPath == null)
        {
            GameSimStatus.Text = "Sanal DualSense HID yolu henüz hazır değil; tetik raporlarını göndermek için köprünün hazır olmasını bekle.";
            return;
        }
        string? micId = ChannelMicEnabled.IsChecked == true && ChannelMicCombo.SelectedItem is LabMicrophone m ? m.Id : null;
        var run = new CancellationTokenSource();
        _gameSimCancel = run;
        GameSimBtn.IsEnabled = false;
        GameSimCombo.IsEnabled = false;
        var (h0, o0, t0) = _bridge.VirtualDualSenseCounters;
        long f0 = _bridge.DirectFramesSent;
        _bridge.MarkVirtualHapticPeaks();
        GameSimStatus.Text = "Çalıyor: " + scene.Title + " — " + scene.Hint;
        try
        {
            bool expectsPcm = GameSignalSimulator.UsesPcm(scene.Id);
            string? endpoint = expectsPcm ? await Task.Run(GameSignalSimulator.FindEndpoint) : null;
            run.Token.ThrowIfCancellationRequested();
            if (expectsPcm && endpoint == null) { GameSimStatus.Text = "Tek bir DualSense ses uç noktası bulunamadı. Sanal köprü açık olmalı; fiziksel DualSense bağlıysa test sırasında ayır."; return; }
            long x0 = _bridge.VirtualXboxMotorWrites;
            var progress = new Progress<string>(hint =>
            {
                if (ReferenceEquals(_gameSimCancel, run) && !run.IsCancellationRequested)
                    GameSimStatus.Text = "Çalıyor: " + scene.Title + "\n" + hint;
            });
            var res = await GameSignalSimulator.PlayAsync(scene.Id, endpoint, _bridge.VirtualDualSenseHidPath, micId, run.Token, progress);
            await Task.Delay(300);
            var (h1, o1, t1) = _bridge.VirtualDualSenseCounters;
            long frames = _bridge.DirectFramesSent - f0;
            var (pl, pr) = _bridge.VirtualHapticPeaks;
            bool xbox = _bridge.VirtualDualSenseMode == WolverineControllerMode.Xbox;
            GameSimStatus.Text = GameSimVerdict.Describe(h1 - h0, pl, pr, frames, res.ReportsWritten, t1 - t0, res.HidError, res.MicDb,
                xbox ? _bridge.VirtualXboxMotorWrites - x0 : null, xbox ? _bridge.VirtualXboxStatus : null, expectsPcm);
            DiagnosticsService.Info("Game simulator " + scene.Id + ": " + GameSimStatus.Text.Replace(Environment.NewLine, " | ") + " | " + _bridge.SensaEngineSummary);
        }
        catch (OperationCanceledException) { GameSimStatus.Text = "Durduruldu."; }
        catch (Exception ex) { NoteError("Game simulator", ex); GameSimStatus.Text = "Hata: " + ex.Message; }
        finally { run.Dispose(); _gameSimCancel = null; GameSimBtn.IsEnabled = true; GameSimCombo.IsEnabled = true; }
    }

    private async void DemoSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _starting || DemoSceneCombo.SelectedItem is not ChannelLabScene { Demo: { } demo }) return;
        _busy = true;
        // A running virtual bridge stays up: the demo is played into it like a game.
        bool throughBridge = DemoDevDualSense.IsChecked != true && _bridge.IsVirtualDualSenseRunning;
        var token = throughBridge ? BeginTestKeepingOutputs() : BeginTest();
        DemoStatus.Text = NativeDemoSignals.Caption(demo, 0);
        try
        {
            var progress = new Progress<int>(ms => DemoStatus.Text = NativeDemoSignals.Caption(demo, ms));
            if (DemoDevDualSense.IsChecked == true)
            {
                await PlayDemoOnDualSenseAsync(demo, progress, token);
                DemoStatus.Text = NativeDemoSignals.Title(demo) + " bitti • DualSense (Exclusive; fiziksel yanıt teyit edilmedi)";
            }
            else
            {
                var route = DemoRoutePolicy.Choose(throughBridge, throughBridge ? WolverineControllerMode.NotFound : await Task.Run(WolverineModeControl.Detect), XboxSensa.Enabled);
                var pcm = DualSenseChannelFixtures.Build(new ChannelLabScene("demo:" + demo, NativeDemoSignals.Title(demo), demo));
                var (l, r) = DemoRoutePolicy.HapticChannels(pcm);
                switch (route)
                {
                    case DemoRoute.VirtualBridge:
                        string endpoint = await Task.Run(GameSignalSimulator.FindEndpoint)
                            ?? throw new InvalidOperationException("Sanal DualSense ses uç noktası bulunamadı.");
                        await DemoRouting.PlayToEndpointAsync(endpoint, l, r, progress, token);
                        break;
                    case DemoRoute.XboxBody:
                        await DemoRouting.PlayToXboxBodyAsync(l, r, _settings.PreferredXInputSlot, _settings.OutputLevelPercent, progress, token);
                        break;
                    case DemoRoute.Sensa:
                        if (!await _bridge.PlayDemoAsync(demo, token, progress)) throw new InvalidOperationException(_bridge.SensaStatus);
                        break;
                    default:
                        throw new InvalidOperationException("Wolverine bulunamadı (kablo ya da alıcıyla bağlı ve açık olmalı).");
                }
                DemoStatus.Text = NativeDemoSignals.Title(demo) + " bitti • " + DemoRoutePolicy.Describe(route);
                bool xboxBody = route == DemoRoute.XboxBody ||
                    route == DemoRoute.VirtualBridge && _bridge.VirtualDualSenseMode == WolverineControllerMode.Xbox;
                if (xboxBody)   // the demos' frequencies and left/right need Sensa (PC mode); say so instead of seeming broken
                    DemoStatus.Text += Environment.NewLine + "Xbox modunda gövde motorları yalnız şiddet verir: frekans (30–400 Hz) ve net sol/sağ yoktur. " +
                                       "Sensa'nın frekanslı, ayrık hissi için PC moduna geç: ○ + Menü + A (2 sn).";
                DiagnosticsService.Info("Demo " + demo + " played via " + route + ".");
            }
            if (DemoDevDualSense.IsChecked == true) DiagnosticsService.Info("Demo " + demo + " played on DualSense.");
        }
        catch (OperationCanceledException) { DemoStatus.Text = "Durduruldu."; }
        catch (Exception ex)
        {
            NoteError("Native demo", ex);
            DemoStatus.Text = "Çalınamadı: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetTestButtons(true);
        }
    }

    /// <summary>Plays a demo's PCM directly on the USB DualSense (reference), keeping its haptic path awake.</summary>
    private static async Task PlayDemoOnDualSenseAsync(NativeDemo demo, IProgress<int> progress, CancellationToken token)
    {
        var endpoint = await Task.Run(NativeBridgeService.FindDualSenseEndpoint) ??
            throw new InvalidOperationException("4 kanallı DualSense ses aygıtı bulunamadı; DualSense'i USB ile tak.");
        var pcm = DualSenseChannelFixtures.Build(new ChannelLabScene("demo:" + demo, NativeDemoSignals.Title(demo), demo));
        using var keepAliveStop = new CancellationTokenSource();
        var keepAlive = Task.Run(() => { try { DualSenseHidControl.KeepAwake(keepAliveStop.Token); } catch { } });
        var sw = Stopwatch.StartNew();
        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ticker = Task.Run(async () =>
        {
            while (!progressStop.IsCancellationRequested)
            {
                progress.Report((int)sw.ElapsedMilliseconds);
                try { await Task.Delay(50, progressStop.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            await Task.Delay(150, token);
            sw.Restart();
            await DualSensePatternPlayer.PlayPcm16Async(endpoint.Id, pcm, token);
        }
        finally
        {
            progressStop.Cancel();
            keepAliveStop.Cancel();
            await keepAlive;
            await ticker;
        }
    }

    private async Task RunSensaTestAsync(HapticTestChannel? side)
    {
        if (DevXbox.IsChecked == true) { await RunXInputTestAsync(side); return; }
        // Xbox mode without the XboxSensa setting has no Sensa route: test the body motors instead of failing.
        if (DevWolverine.IsChecked == true && !_busy && !_starting && !XboxSensa.Enabled &&
            await Task.Run(WolverineModeControl.Detect) == WolverineControllerMode.Xbox)
        {
            await RunXInputTestAsync(side);
            SensaTestStatus.Text = "Kol Xbox modunda ve 'Xbox modunda Sensa' ayarı kapalı: gövde motorları test edildi.";
            return;
        }
        if (_busy || _starting) return;
        _busy = true;
        var token = BeginTest();
        SensaAskPanel.Visibility = Visibility.Collapsed;
        string label = side == HapticTestChannel.Left ? "Sol" : side == HapticTestChannel.Right ? "Sağ" : "İki taraf";
        bool dualSense = DevDualSense.IsChecked == true;
        string device = dualSense ? "DualSense" : "Wolverine";
        SensaTestStatus.Text = device + " · " + label + " deseni çalıyor…";
        try
        {
            if (dualSense)
            {
                var endpoint = await Task.Run(NativeBridgeService.FindDualSenseEndpoint) ??
                    throw new InvalidOperationException("4 kanallı DualSense ses aygıtı bulunamadı; DualSense'i USB ile tak.");
                // Wake the haptic path (power-save / haptic mute off). Live controller state only.
                try
                {
                    string hidPath = await Task.Run(DualSenseHidControl.WakeHaptics);
                    DiagnosticsService.Info("DualSense haptic wake report (AllowAudioMute, MuteControl=0) sent to " + hidPath);
                }
                catch (Exception wakeError)
                {
                    DiagnosticsService.Error("DualSense haptic wake", wakeError);
                    SensaTestStatus.Text = "Uyarı: DualSense uyandırma raporu gönderilemedi (" + wakeError.Message + "). Yine de çalınıyor…";
                }
                // Keep the haptic path alive with output reports every 20 ms while the pattern plays.
                using var keepAliveStop = new CancellationTokenSource();
                var keepAlive = Task.Run(() => DualSenseHidControl.KeepAwake(keepAliveStop.Token));
                try
                {
                    await Task.Delay(150, token); // let the controller power the path before audio
                    await DualSensePatternPlayer.PlayAsync(endpoint.Id,
                        side != HapticTestChannel.Right, side != HapticTestChannel.Left, token);
                }
                finally
                {
                    keepAliveStop.Cancel();
                    try { DiagnosticsService.Info("DualSense keep-alive reports sent: " + await keepAlive); }
                    catch (Exception keepError) { DiagnosticsService.Error("DualSense keep-alive", keepError); }
                }
                DiagnosticsService.Info("DualSense reference pattern " + label + " played on rear channel(s) of " +
                    endpoint.Name + " (Exclusive PCM16 quad; physical response not verified)");
            }
            else
            {
                bool ok = side is { } s ? await _bridge.TestSideAsync(s, token) : await _bridge.TestBothAsync(token);
                if (!ok) throw new InvalidOperationException(_bridge.SensaStatus);
                DiagnosticsService.Info("Sensa test pattern " + label + " sent (target " +
                    (side == HapticTestChannel.Right ? SensaStereoPrototype.RightTarget.Label : SensaStereoPrototype.LeftTarget.Label) + ").");
            }
            SensaTestStatus.Text = device + " · " + label + " deseni çalındı" +
                (dualSense ? " (Exclusive PCM16; fiziksel yanıt teyidi bekleniyor)." : ".");
            if (side is { } asked)
            {
                _askingSide = asked;
                _askingDualSense = dualSense;
                SensaAskText.Text = device + " · " + label + " — nerede hissettin?";
                SensaAskPanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { SensaTestStatus.Text = "Test durduruldu."; }
        catch (Exception ex)
        {
            NoteError("Sensa test", ex);
            SensaTestStatus.Text = "Gönderilemedi: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SetTestButtons(true);
        }
    }

    /// <summary>
    /// Audio-haptics check: runs the "Sesten titreşim" route for the controller's current mode
    /// and plays a bass tone gliding left → right → left on the default playback device.
    /// </summary>
    private async void XboxCombinedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _starting || _gameSimCancel != null) return;
        _busy = true;
        var token = BeginTest();
        SensaAskPanel.Visibility = Visibility.Collapsed;
        try
        {
            var status = new Progress<string>(phase => SensaTestStatus.Text = "Xbox test · " + phase);
            if (ReferenceEquals(sender, XboxTriggerBackendBtn))
            {
                SensaTestStatus.Text = "Tetik çıkış testi hazırlanıyor…";
                await Task.Run(() => XboxAudioGameBridge.TestTriggerBackendsAsync(_settings.OutputLevelPercent, status, token), token);
                SensaTestStatus.Text = "LT/RT çıkış testi bitti. Hangi çıkışta ve hangi tarafta titreşim hissettiğini kaydet.";
            }
            else
            {
                var result = await Task.Run(() => XboxAudioGameBridge.RunSyntheticTestAsync(
                    _settings.AudioProfile, _settings.OutputLevelPercent, status, token), token);
                SensaTestStatus.Text = $"Sentetik test bitti · {result.Reports} rapor · {result.Writes} GameInput yazımı. Motor hissini aşamalara göre değerlendir.";
            }
            DiagnosticsService.Info(SensaTestStatus.Text);
        }
        catch (OperationCanceledException) { SensaTestStatus.Text = "Xbox testi durduruldu."; }
        catch (Exception ex) { NoteError("Xbox birleşik test", ex); SensaTestStatus.Text = "Xbox testi çalışmadı: " + ex.Message; }
        finally { _busy = false; SetTestButtons(true); }
    }

    private async void AudioPan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var kind = sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AudioTestKind>(tag, out var k) ? k : AudioTestKind.Glide;
        _busy = true;
        var token = BeginTest();
        SensaAskPanel.Visibility = Visibility.Collapsed;
        AudioHapticsBridge? xbox = null;
        try
        {
            var mode = await Task.Run(WolverineModeDetector.Detect);
            string route;
            if (mode == WolverineMode.Pc)
            {
                // The test follows the on-screen caption, not a speaker: no Bluetooth sync delay.
                await _bridge.StartAudioToSensaAsync(token, null, 0, applySensitivity: false, profile: _settings.AudioProfile);
                route = "PC modu → Sensa motorları";
            }
            else if (mode == WolverineMode.Xbox)
            {
                xbox = new AudioHapticsBridge(new XboxAudioSink { OutputLevelPercent = _settings.OutputLevelPercent, PreferredIndex = _settings.PreferredXInputSlot }) { DelayOverrideMs = 0, Profile = _settings.AudioProfile, ApplyLevelShaping = false };
                xbox.Start();
                route = "Xbox modu → yalnız gövde motorları";
            }
            else throw new InvalidOperationException("Wolverine bulunamadı (PC veya Xbox modunda bağlı olmalı).");
            await Task.Delay(300, token);
            var progress = new Progress<int>(ms => SensaTestStatus.Text = AudioPanTest.Caption(kind, ms) + "  (" + route + ")");
            await AudioPanTest.PlayAsync(kind, progress, token);
            await Task.Delay(400, token);   // let the last frames finish before the engine stops
            string expect = kind switch
            {
                AudioTestKind.PingPong => "Titreşim sırayla tamamen sol, tamamen sağ elde hissedilmeli.",
                AudioTestKind.Directions8 => "Sağ/sol konumla, ön keskin tıkırtıyla, arka derin uğultuyla hissedilmeli.",
                AudioTestKind.FrontBack => "Ön: keskin hızlı tıkırtı, arka: derin yumuşak uğultu; ikisi de iki elde.",
                _ => "Bas önce sol elde, sonra sağda, sonra yine solda hissedilmeli."
            };
            SensaTestStatus.Text = AudioPanTest.Title(kind) + " bitti (" + route + "). " + expect + " " + SynapseA2HWarning;
            DiagnosticsService.Info("Audio test " + kind + " played via " + route + ".");
        }
        catch (OperationCanceledException) { SensaTestStatus.Text = "Ses testi durduruldu."; }
        catch (Exception ex)
        {
            NoteError("Audio pan test", ex);
            SensaTestStatus.Text = "Ses testi çalışmadı: " + ex.Message;
        }
        finally
        {
            xbox?.Dispose();
            _bridge.Stop();
            _busy = false;
            SetTestButtons(true);
        }
    }

    /// <summary>
    /// Synapse's own "Audio to Haptics" also turns all PC audio into Sensa vibration (with its own
    /// delay and long tail). Measured 2026-11-07: with RW3's engine OFF, the test tone still made the
    /// controller vibrate for 6+ s after the audio ended. It must be off for RW3 audio modes.
    /// </summary>
    private const string SynapseA2HWarning =
        "Titreşim gecikiyor ya da test bitince sürüyorsa başka bir ses→titreşim aracı çalışıyor: " +
        "Synapse 'Audio to Haptics' veya DSX 'Audio to Haptics' (yakındaki DualSense'i titretir). İkisini de kapat.";

    private async void SensaReset_Click(object sender, RoutedEventArgs e)
    {
        // Works even mid-test: cancel, fully quit HAR + Razer provider, then start clean.
        _testCancel?.Cancel();
        SensaAskPanel.Visibility = Visibility.Collapsed;
        _askingSide = null;
        SensaResetBtn.IsEnabled = false;
        SensaTestStatus.Text = "Sıfırlanıyor…";
        try
        {
            StopAll();
            await Task.Run(_bridge.Stop);
            await _bridge.EnsureSensaAsync();
            SensaTestStatus.Text = "Motor sıfırlandı ve temiz başlatıldı. Tekrar dene.";
            DiagnosticsService.Info("User reset Sensa engine from Test tab.");
        }
        catch (Exception ex)
        {
            NoteError("Sensa reset", ex);
            SensaTestStatus.Text = "Sıfırlanamadı: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SensaResetBtn.IsEnabled = true;
            SetTestButtons(true);
        }
    }

    private enum SideFeel { Untested, Clean, Leak, NotFelt }
    private readonly Dictionary<(bool DualSense, HapticTestChannel Side), SideFeel> _feel = new();
    private bool _askingDualSense;

    private void SensaFeltClean_Click(object sender, RoutedEventArgs e) => RecordFelt(SideFeel.Clean);
    private void SensaFeltLeak_Click(object sender, RoutedEventArgs e) => RecordFelt(SideFeel.Leak);
    private void SensaFeltNo_Click(object sender, RoutedEventArgs e) => RecordFelt(SideFeel.NotFelt);

    private void TestDevice_Checked(object sender, RoutedEventArgs e)
    {
        if (SensaAskPanel == null) return;
        SensaAskPanel.Visibility = Visibility.Collapsed;
        _askingSide = null;
        SensaResetBtn.Visibility = DevWolverine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordFelt(SideFeel feel)
    {
        if (_askingSide is not { } side) return;
        _feel[(_askingDualSense, side)] = feel;
        DiagnosticsService.Info("User physical rating: " + (_askingDualSense ? "DualSense" : "Wolverine") +
            " " + side + " = " + feel + (_askingDualSense ? "" :
            " (targets " + SensaStereoPrototype.LeftTarget.Label + " / " + SensaStereoPrototype.RightTarget.Label + ")"));
        _askingSide = null;
        SensaAskPanel.Visibility = Visibility.Collapsed;
        ShowFeelResults();
    }

    private void ShowFeelResults()
    {
        SensaWolverineResult.Text = "Wolverine   " + FeelText(false);
        SensaDualSenseResult.Text = "DualSense   " + FeelText(true);
    }

    private string FeelText(bool dualSense) =>
        "Sol: " + Describe(_feel.GetValueOrDefault((dualSense, HapticTestChannel.Left))) +
        "    Sağ: " + Describe(_feel.GetValueOrDefault((dualSense, HapticTestChannel.Right)));

    private static string Describe(SideFeel feel) => feel switch
    {
        SideFeel.Clean => "✓ sadece bu tarafta",
        SideFeel.Leak => "⚠ iki tarafa sızıyor",
        SideFeel.NotFelt => "✕ hissedilmedi",
        _ => "–"
    };

    // ───────────────────────── controller control ─────────────────────────
    // The whole app can be driven from the controller:
    //   LB/RB = tabs · D-pad/left stick = move focus · A = select · B = back/cancel
    //   Menu (☰) = next mode. Test prompts take priority over navigation.
    // Input is only acted on while one of THIS app's windows is in the foreground,
    // so presses made while playing a game never trigger anything here.

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private void PadTimer_Tick(object? sender, EventArgs e)
    {
        ushort buttons = XInputHaptics.ReadButtonsAnyPad();
        ushort pressed = (ushort)(buttons & ~_prevPadButtons);
        _prevPadButtons = buttons;
        if (pressed == 0 || !IsVisible) return;
        try { HandlePadPress(pressed); }
        catch (Exception ex) { NoteError("Controller input", ex); }
    }

    private static bool Has(ushort pressed, ushort mask) => (pressed & mask) != 0;

    private void HandlePadPress(ushort p)
    {
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid != (uint)Environment.ProcessId) return;      // a game or other app has focus
        IntPtr main = new WindowInteropHelper(this).Handle;
        if (fg != main)
        {
            ForwardPadToForeignWindow(p);                    // MessageBox or a secondary window
            return;
        }

        // 0) General calibration owns the controller while its panel is up.
        if (CalibPanel.Visibility == Visibility.Visible)
        {
            int answer = Has(p, XInputHaptics.ButtonX) ? 0 : Has(p, XInputHaptics.ButtonA) ? 1 : Has(p, XInputHaptics.ButtonB) ? 2
                : Has(p, XInputHaptics.ButtonY) ? 3 : Has(p, XInputHaptics.ButtonView) ? CalibReplay
                : Has(p, XInputHaptics.ButtonMenu) ? CalibQuit : int.MinValue;
            if (answer != int.MinValue) _calibAnswer?.TrySetResult(answer);
            return;
        }

        // 1) Test prompts waiting for an answer.
        if (TestPage.Visibility == Visibility.Visible)
        {
            if (SensaAskPanel.Visibility == Visibility.Visible && !_busy)
            {
                if (Has(p, XInputHaptics.ButtonA)) { RecordFelt(SideFeel.Clean); return; }
                if (Has(p, XInputHaptics.ButtonX)) { RecordFelt(SideFeel.Leak); return; }
                if (Has(p, XInputHaptics.ButtonB)) { RecordFelt(SideFeel.NotFelt); return; }
            }
        }

        // 2) Global navigation.
        if (Has(p, XInputHaptics.ButtonLeftShoulder)) { SwitchTab(-1); return; }
        if (Has(p, XInputHaptics.ButtonRightShoulder)) { SwitchTab(+1); return; }
        if (Has(p, XInputHaptics.ButtonMenu)) { CycleMode(); return; }
        if (Has(p, XInputHaptics.ButtonB))
        {
            if (_busy) _testCancel?.Cancel();                // stop a running test/demo
            else if (TabGeneral.IsChecked != true) TabGeneral.IsChecked = true;
            return;
        }
        if (Has(p, XInputHaptics.ButtonDpadUp)) { MoveFocusPad(FocusNavigationDirection.Up); return; }
        if (Has(p, XInputHaptics.ButtonDpadDown)) { MoveFocusPad(FocusNavigationDirection.Down); return; }
        if (Has(p, XInputHaptics.ButtonDpadLeft)) { MoveFocusPad(FocusNavigationDirection.Left); return; }
        if (Has(p, XInputHaptics.ButtonDpadRight)) { MoveFocusPad(FocusNavigationDirection.Right); return; }
        if (Has(p, XInputHaptics.ButtonA)) ActivateFocused();
    }

    private FrameworkElement CurrentPage =>
        TabTest.IsChecked == true ? TestPage : TabSettings.IsChecked == true ? SettingsPage : GeneralPage;

    private void SwitchTab(int delta)
    {
        var tabs = new[] { TabGeneral, TabTest, TabSettings };
        int current = Array.FindIndex(tabs, t => t.IsChecked == true);
        int next = (current + delta + tabs.Length) % tabs.Length;
        tabs[next].IsChecked = true;
        FocusFirstIn(CurrentPage);
    }

    private void FocusFirstIn(FrameworkElement page) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // On Genel the natural starting point is the selected mode card.
            if (ReferenceEquals(page, GeneralPage))
            {
                var mode = ModeAudio.IsChecked == true ? ModeAudio :
                           ModeNative.IsChecked == true ? ModeNative : ModeNormal;
                if (mode.Focus()) return;
            }
            Keyboard.Focus(page);
            page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }));

    private void MoveFocusPad(FocusNavigationDirection direction)
    {
        // Nothing focused yet (or focus is outside the visible page): start at its first item.
        if (Keyboard.FocusedElement is not UIElement focused || !focused.IsVisible ||
            (!CurrentPage.IsAncestorOf((DependencyObject)focused) && !IsTabButton(focused)))
        {
            FocusFirstIn(CurrentPage);
            return;
        }
        focused.MoveFocus(new TraversalRequest(direction));
    }

    private bool IsTabButton(object element) =>
        ReferenceEquals(element, TabGeneral) || ReferenceEquals(element, TabTest) || ReferenceEquals(element, TabSettings);

    private static void ActivateFocused()
    {
        switch (Keyboard.FocusedElement)
        {
            case RadioButton { IsEnabled: true } rb:
                rb.IsChecked = true;
                break;
            case System.Windows.Controls.CheckBox { IsEnabled: true } cb:
                cb.IsChecked = cb.IsChecked != true;
                break;
            case System.Windows.Controls.Button { IsEnabled: true } b:
                b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
                break;
            case System.Windows.Controls.ComboBox { IsEnabled: true, Items.Count: > 0 } combo:
                combo.SelectedIndex = (combo.SelectedIndex + 1) % combo.Items.Count;
                break;
        }
    }

    /// <summary>
    /// Message boxes and the advanced windows are driven with synthetic keys:
    /// A = Space (press focused button), B = Esc, D-pad = arrow keys / Tab.
    /// </summary>
    private static void ForwardPadToForeignWindow(ushort p)
    {
        string? keys =
            Has(p, XInputHaptics.ButtonA) ? " " :
            Has(p, XInputHaptics.ButtonB) ? "{ESC}" :
            Has(p, XInputHaptics.ButtonDpadLeft) ? "+{TAB}" :
            Has(p, XInputHaptics.ButtonDpadRight) ? "{TAB}" :
            Has(p, XInputHaptics.ButtonDpadUp) ? "+{TAB}" :
            Has(p, XInputHaptics.ButtonDpadDown) ? "{TAB}" : null;
        if (keys != null) Forms.SendKeys.SendWait(keys);
    }

    // ───────────────────────── Test: general calibration (blind, controller-driven) ─────────────────────────

    private const int CalibReplay = -1, CalibQuit = -2;
    private TaskCompletionSource<int>? _calibAnswer;
    private static string CalibrationFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "calibration");

    private void Calibration_Click(object sender, RoutedEventArgs e) => _ = RunCalibrationAsync();

    private void CalibrationFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(CalibrationFolder); Process.Start(new ProcessStartInfo("explorer.exe", CalibrationFolder) { UseShellExecute = true }); }
        catch (Exception ex) { NoteError("Kalibrasyon klasörü", ex); }
    }

    private void CalibOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int i)) _calibAnswer?.TrySetResult(i);
    }

    private void CalibKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (CalibPanel.Visibility != Visibility.Visible) return;
        int answer = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3,
            Key.R => CalibReplay, Key.Escape => CalibQuit, Key.Enter or Key.Space => 1, _ => int.MinValue
        };
        if (answer == int.MinValue) return;
        _calibAnswer?.TrySetResult(answer);
        e.Handled = true;
    }

    private async Task<int> AwaitCalibAnswerAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calibAnswer = tcs;
        using (token.Register(() => tcs.TrySetCanceled()))
        {
            try { return await tcs.Task; }
            finally { _calibAnswer = null; }
        }
    }

    private void ShowCalibOptions(IReadOnlyList<string>? options)
    {
        CalibOptions.Visibility = options == null ? Visibility.Hidden : Visibility.Visible;
        if (options == null) return;
        CalibOptText0.Text = options[0]; CalibOptText1.Text = options[1]; CalibOptText2.Text = options[2];
        bool four = options.Count > 3;                   // locate trials add "Orta" (Y); 1-or-2 trials keep 3
        CalibOpt3.Visibility = four ? Visibility.Visible : Visibility.Collapsed;
        CalibOptions.Columns = four ? 4 : 3;
        CalibOptText3.Text = four ? options[3] : "";
    }

    private async Task PlayCalibTrialAsync(CalibrationRig rig, CalTrial t, string progress, CancellationToken token)
    {
        ShowCalibOptions(null);
        CalibProgress.Text = progress;
        if (t.Second == null)
        {
            CalibQuestion.Text = "Dikkat…"; await Task.Delay(700, token);
            CalibQuestion.Text = "● ÇALIYOR"; await rig.PlayAsync(t.First, token);
        }
        else
        {
            CalibQuestion.Text = "Dikkat…  (iki titreşim gelecek)"; await Task.Delay(700, token);
            CalibQuestion.Text = "1"; await rig.PlayAsync(t.First, token);
            CalibQuestion.Text = "…"; await Task.Delay(800, token);
            CalibQuestion.Text = "2"; await rig.PlayAsync(t.Second, token);
        }
    }

    /// <summary>Blind general calibration: shuffled locate and 1-or-2 trials, answered with X/A/B on the controller.
    /// Raw data (JSON lines) and a summary are saved under %LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration.</summary>
    private async Task RunCalibrationAsync()
    {
        if (_busy || _starting) return;
        _busy = true;
        var token = BeginTest();
        CalibPanel.Visibility = Visibility.Visible;
        CalibSummary.Text = ""; ShowCalibOptions(null);
        CalibrationRig? rig = null;
        var results = new List<CalResult>();
        CalCapabilities? caps = null;
        bool saved = false;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        void Save()
        {
            if (saved || results.Count == 0 || caps == null) return;
            saved = true;
            Directory.CreateDirectory(CalibrationFolder);
            string raw = Path.Combine(CalibrationFolder, $"calibration-{stamp}.jsonl"), sum = Path.Combine(CalibrationFolder, $"calibration-{stamp}-ozet.txt");
            File.WriteAllLines(raw, results.Select((r, i) => CalibrationScoring.ToJsonLine(i + 1, r)));
            string text = CalibrationScoring.Summarize(results, caps);
            File.WriteAllText(sum, text);
            CalibSummary.Text = text;
            CalibStatus.Text = $"Son kalibrasyon: {results.Count} deneme, {caps.Mode} modu → {sum}";
            DiagnosticsService.Info("General calibration saved: " + raw + Environment.NewLine + text);
        }
        try
        {
            CalibProgress.Text = "Genel kalibrasyon testi";
            CalibQuestion.Text = "Kol hazırlanıyor…";
            await Task.Delay(500, token);                 // let the stopped modes release the devices
            rig = await CalibrationRig.OpenAsync(token);
            caps = rig.Caps;
            if (!caps.Any)
            {
                CalibQuestion.Text = "Wolverine bulunamadı (PC ya da Xbox modunda bağlı olmalı). Çıkmak için Ⓐ.";
                await AwaitCalibAnswerAsync(token);
                return;
            }
            var plan = CalibrationPlan.Build(caps, Environment.TickCount);
            string parts = string.Join(", ", new[] { caps.SensaPalms ? "avuçlar" : null, caps.SensaTriggers || caps.RumbleTriggers ? "tetikler" : null, caps.Body ? "saplar" : null }.Where(x => x != null));
            CalibQuestion.Text = $"{caps.Mode} modu · {parts}\n{plan.Count} deneme, yaklaşık {Math.Ceiling(plan.Count * 8 / 60.0)} dakika.\n" +
                                 "Kolu oyun oynar gibi iki elinle tut. Seçenekler her denemede değişir; bazen iki yer birden titrer.\n" +
                                 "Hissettin ama yerini ayıramadıysan Ⓨ = Orta. Tekrar çalmak için ⧉.\nHazır olunca Ⓐ";
            if (await AwaitCalibAnswerAsync(token) == CalibQuit) return;
            for (int i = 0; i < plan.Count; i++)
            {
                var t = plan[i];
                int replays = 0, answer;
                double ms;
                while (true)
                {
                    await PlayCalibTrialAsync(rig, t, $"Deneme {i + 1}/{plan.Count}", token);
                    CalibQuestion.Text = t.Question;
                    ShowCalibOptions(t.Options);
                    var sw = Stopwatch.StartNew();
                    do answer = await AwaitCalibAnswerAsync(token);
                    while (answer >= t.Options.Count);          // Y on a 3-option trial: no such answer, keep waiting
                    ms = sw.Elapsed.TotalMilliseconds;
                    if (answer != CalibReplay) break;
                    replays++;
                }
                if (answer == CalibQuit) break;
                results.Add(new CalResult(t, answer, ms, replays));
                ShowCalibOptions(null);
                await Task.Delay(400, token);
            }
            Save();
            CalibProgress.Text = "Bitti";
            CalibQuestion.Text = results.Count > 0 ? "Sonuçlar kaydedildi. Kapatmak için Ⓐ" : "Hiç cevap yok. Kapatmak için Ⓐ";
            await AwaitCalibAnswerAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { NoteError("Kalibrasyon", ex); CalibStatus.Text = "Kalibrasyon hatası: " + ex.Message; }
        finally
        {
            try { Save(); } catch (Exception ex) { NoteError("Kalibrasyon kaydı", ex); }
            rig?.Dispose();
            CalibPanel.Visibility = Visibility.Collapsed;
            _busy = false;
            SetTestButtons(true);
        }
    }


    // ───────────────────────── Test: XInput (classic) + A/B ─────────────────────────

    private async Task RunXInputTestAsync(HapticTestChannel? side)
    {
        if (_busy || _starting) return;
        _busy = true;
        var token = BeginTest();
        SensaAskPanel.Visibility = Visibility.Collapsed;
        string label = side == HapticTestChannel.Left ? "Sol" : side == HapticTestChannel.Right ? "Sağ" : "İki";
        try
        {
            bool ok = side is { } s1 ? await _rumble.TestChannelAsync(s1, token) : await _rumble.TestAsync();
            SensaTestStatus.Text = ok
                ? "Xbox · " + label + " motor komutu gönderildi. Hissetmediysen Ayarlar'dan kontrolcü yuvasını değiştir."
                : "Xbox kontrolcüsü bulunamadı. Kolun bağlı ve açık olduğunu, Ayarlar'daki yuvanın doğru olduğunu kontrol et.";
        }
        catch (OperationCanceledException) { SensaTestStatus.Text = "Test durduruldu."; }
        catch (Exception ex) { NoteError("XInput test", ex); SensaTestStatus.Text = ex.Message; }
        finally
        {
            _busy = false;
            SetTestButtons(true);
        }
    }

    // ───────────────────────── Ayarlar ─────────────────────────

    private void SelectLevelPreset(int percent)
    {
        RadioButton best = Level100;
        int bestDiff = int.MaxValue;
        foreach (var rb in new[] { Level25, Level50, Level75, Level100 })
        {
            int diff = Math.Abs(int.Parse((string)rb.Tag) - percent);
            if (diff < bestDiff) { bestDiff = diff; best = rb; }
        }
        best.IsChecked = true;
    }

    private void Level_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int next = HapticOutputLevel.Limit(int.Parse(tag));
        _settings.OutputLevelPercent = next;
        _rumble.OutputLevelPercent = next;
        _bridge.OutputLevelPercent = next;
        _xboxAudio?.SetXboxOutputLevel(next);
        if (_combinedXbox != null) _combinedXbox.OutputLevel = next;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Output level", ex); }
    }

    private void NativeGain_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int gain = int.Parse(tag);
        _settings.NativeGainPercent = gain;
        _bridge.SourceGainPercent = gain;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Native gain", ex); }
        DiagnosticsService.Info("Native v2 source gain " + gain + "%.");
    }

    private void AudioSync_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int ms = int.Parse(tag);
        _settings.AudioSyncDelayMs = ms;
        AudioHapticsBridge.SyncDelaySetting = ms;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Audio sync", ex); }
        DiagnosticsService.Info("Audio sync delay setting " + ms + " ms.");
        if (_run == RunState.Audio) { StopAll(); _ = EnsureModeRunningAsync(); }   // apply now
        UpdateAudioSyncStatus();
    }

    private void SettingsView_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsBasicPanel == null) return;
        bool advanced = SettingsAdvancedTab.IsChecked == true;
        SettingsBasicPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        SettingsAdvancedPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        if (advanced) { UpdateAudioSyncStatus(); UpdateOutputMethodStatus(); }
    }

    private void OutputMethod_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int method = int.Parse(tag);
        if (method == _settings.OutputMethod) return;
        if (_busy || _starting)
        {
            ((SensaOutputMethod)_settings.OutputMethod switch
                { SensaOutputMethod.Hybrid => MethodHybrid, SensaOutputMethod.Multiband => MethodMultiband, _ => MethodStandard }).IsChecked = true;
            return;
        }
        StopAll();
        _bridge.OutputMethod = (SensaOutputMethod)method;
        _settings.OutputMethod = method;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Output method", ex); }
        DiagnosticsService.Info("Wolverine output method: " + (SensaOutputMethod)method + ".");
        UpdateOutputMethodStatus();
        _ = EnsureModeRunningAsync();
    }

    private void Transport_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int transport = int.Parse(tag);
        if (transport == _settings.Transport) return;
        if (_busy || _starting) { (_settings.Transport == 1 ? TransportDirect : TransportSynapse).IsChecked = true; return; }
        StopAll();
        _bridge.Transport = (SensaTransport)transport;
        _settings.Transport = transport;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Transport", ex); }
        DiagnosticsService.Info("Sensa transport: " + (SensaTransport)transport + ".");
        UpdateOutputMethodStatus();
        _ = EnsureModeRunningAsync();
    }

    private void UpdateTransportStatus()
    {
        if (TransportStatus == null) return;
        if (_settings.Transport == 1 || _bridge.TransportFallbackReason != null)
        {
            var paths = SensaHidTransport.Candidates();
            string prefix = _settings.Transport == 1 ? "" : "Synapse seçili ama kullanılamıyor (" + _bridge.TransportFallbackReason + ") → ";
            TransportStatus.Text = prefix + (paths.Count > 0
                ? "Doğrudan USB: Wolverine bulundu" + (_bridge.DirectFramesSent > 0 ? $" • {_bridge.DirectFramesSent} kare gönderildi" : "") + "."
                : "Doğrudan USB: Wolverine bulunamadı. Kolu USB kablosu ya da kendi alıcısıyla bağla ve PC modunda olduğundan emin ol.");
        }
        else
        {
            bool attached = SynapseSensaCheck.LooksAttached(out string detail);
            TransportStatus.Text = attached ? "Synapse yolu: " + detail
                : "Synapse kolu titreşim servisine bağlamamış görünüyor (" + detail + "). Synapse'te Wolverine'in Sensa sayfasını aç ya da Doğrudan USB'yi seç.";
        }
    }

    private void UpdateOutputMethodStatus()
    {
        UpdateTransportStatus();
        var (multi, standard) = _bridge.HybridFrames;
        OutputMethodStatus.Text = (SensaOutputMethod)_settings.OutputMethod switch
        {
            SensaOutputMethod.Hybrid => "Hibrit açık" + (multi + standard > 0 ? $" • son çalışmada karelerin %{100.0 * multi / (multi + standard):F0}'i çok bantlı" : "") + ".",
            SensaOutputMethod.Multiband => "Çok bantlı açık: kısa vuruşlar zayıf hissedilebilir.",
            _ => "Standart (önerilen)."
        };
    }

    private void AudioSensitivity_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        int sens = int.Parse(tag);
        _settings.AudioSensitivityPercent = sens;
        _bridge.AudioSensitivityPercent = sens;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Audio sensitivity", ex); }
        DiagnosticsService.Info("Audio-to-haptics sensitivity " + sens + "%.");
    }

    private void UpdateAudioSyncStatus()
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            bool bt = AudioHapticsBridge.IsBluetooth(dev);
            int ms = AudioSyncDelay.Resolve(_settings.AudioSyncDelayMs, bt);
            AudioSyncStatus.Text = "Çıkış: " + dev.FriendlyName + (bt ? " (Bluetooth)" : "") + " → +" + ms + " ms";
        }
        catch { AudioSyncStatus.Text = ""; }
    }

    private void DeviceSlot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || DeviceSlotCombo.SelectedIndex < 0) return;
        int slot = DeviceSlotCombo.SelectedIndex - 1;
        _settings.PreferredXInputSlot = slot;
        _rumble.PreferredIndex = slot;
        if (_run == RunState.Audio) StopAll(); // never keep writing rumble to a changed target
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("XInput slot", ex); }
        DiagnosticsService.Info("User selected XInput slot: " + (slot < 0 ? "auto" : slot.ToString()));
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        try { StartupService.SetEnabled(AutostartCheckbox.IsChecked == true); }
        catch (Exception ex)
        {
            NoteError("Autostart", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Otomatik başlatma", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartCheckbox.IsChecked = StartupService.IsEnabled;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText.Text = "Kontrol ediliyor…";
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _availableUpdate = await _updates.CheckAsync();
            if (_availableUpdate == null)
                UpdateStatusText.Text = "En güncel sürümü kullanıyorsun.";
            else
            {
                UpdateStatusText.Text = "Yeni sürüm var: v" + _availableUpdate.Version;
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { NoteError("Release check", ex); UpdateStatusText.Text = "Kontrol edilemedi: " + ex.Message; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        DownloadUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "İndiriliyor ve SHA256 doğrulanıyor…";
        try
        {
            var file = await _updates.DownloadVerifiedAsync(_availableUpdate);
            UpdateStatusText.Text = "İndirildi ve doğrulandı.";
            if (_availableUpdate.IsPortable)
            {
                System.Windows.MessageBox.Show(this,
                    "Taşınabilir sürüm İndirilenler klasörüne kaydedildi. Uygulamadan çıkıp ZIP'i yeni bir klasöre aç.",
                    "Güncelleme", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenPath(Path.GetDirectoryName(file)!);
            }
            else if (System.Windows.MessageBox.Show(this,
                         "Kurulum dosyası doğrulandı. Uygulamadan çıkıp kurulsun mu?",
                         "Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                OpenPath(file);
                ExitCompletely();
            }
        }
        catch (Exception ex) { NoteError("Release download", ex); UpdateStatusText.Text = "Başarısız: " + ex.Message; }
        finally { DownloadUpdateButton.IsEnabled = true; }
    }

    private void CopyStatus_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snap = _rumble.Snapshot();
            string report = "RW3 Pro Haptics Diagnostics\n" +
                "Timestamp: " + DateTimeOffset.Now.ToString("O") + "\n" +
                "Version: " + UpdateService.InstalledVersion + "\n" +
                "Mode: " + _settings.Mode + " | Running: " + _run + "\n" +
                "OutputLevelPercent: " + _settings.OutputLevelPercent + "\n" +
                "NativeGainPercent: " + _settings.NativeGainPercent + " | EngineTickHz: " + _bridge.EngineTickHz.ToString("F0") + "\n" +
                "SensaStatus: " + _bridge.SensaStatus + "\n" +
                "SourceStatus: " + _bridge.SourceStatus + " (" + (_bridge.SourceName ?? "none") + ")\n" +
                "UserFeel Wolverine: " + FeelText(false) + "\n" +
                "UserFeel DualSense: " + FeelText(true) + "\n" +
                "AudioCallbacks: " + snap.Callbacks + " | XInputAccepted: " + snap.OutputAccepted + "\n" +
                "Performance: " + ResourceText.Text + "\n" +
                "NativeGameAuthoredPcmVerified: false\n" +
                "NativeOutput: v2 parametric stream (33 ms frames; amplitude/frequency/transients), not the DualSense waveform\n" +
                "LastProblem: " + (_lastErrorNoted ?? "none") + "\n";
            System.Windows.Clipboard.SetText(report);
            DiagnosticsText.Text = "Rapor panoya kopyalandı.";
        }
        catch (Exception ex) { NoteError("Copy diagnostics", ex); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(DiagnosticsService.LogFile)) DiagnosticsService.Info("Log viewed.");
            OpenPath(DiagnosticsService.LogFile);
        }
        catch (Exception ex) { NoteError("Open log", ex); }
    }

    private void Github_Click(object sender, RoutedEventArgs e) => OpenPath("https://github.com/skonyd/rw3pronativedshaptics");
    private void Synapse_Click(object sender, RoutedEventArgs e) => OpenPath("https://www.razer.com/synapse-4");

    // ───────────────────────── helpers ─────────────────────────

    private static void OpenPath(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });

    private void SetAudioPriority(bool enabled)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = enabled ? ProcessPriorityClass.BelowNormal
                : (_originalPriority ?? ProcessPriorityClass.Normal);
        }
        catch (Exception ex) { DiagnosticsService.Error("Process priority", ex); }
    }

    private void NoteError(string where, Exception ex)
    {
        _lastErrorNoted = where + ": " + ex.Message;
        DiagnosticsService.Error(where, ex);
    }
}
