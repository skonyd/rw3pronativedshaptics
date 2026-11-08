using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private Forms.ToolStripMenuItem? _normalMenu, _audioMenu, _nativeMenu;

    private enum RunState { Off, Audio, Native }
    private RunState _run = RunState.Off;
    private bool _busy, _initialized, _realExit;
    private string? _lastErrorNoted;
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
        (_settings.TriggerFeel == 1 ? TriggerFeelMotors : TriggerFeelClassic).IsChecked = true;
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

        (_settings.Mode switch
        {
            HapticMode.Audio => ModeAudio,
            HapticMode.Native => ModeNative,
            _ => ModeNormal
        }).IsChecked = true;
        _initialized = true;
        if (Environment.GetCommandLineArgs().Contains("--start-virtual-dualsense", StringComparer.OrdinalIgnoreCase))
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
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
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

    private async Task RefreshVirtualControllerAsync(string? lastAction = null)
    {
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
        VcBridgeBtn.IsEnabled = st.Ready && !_vcBusy;
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
        PadModeText.Text = (note != null ? note + Environment.NewLine : "") + m switch
        {
            WolverineControllerMode.Pc => "PC modu: avuçlarda Sensa HD titreşim; DualSense tetik efektleri gövde motorlarında. Tetiklerin içinde hissetmek için Xbox moduna geç.",
            WolverineControllerMode.Xbox => "Xbox modu: DualSense tetik efektleri tetik motorlarında, titreşim sesi gövde motorlarında (bu modda Sensa yok). " + WolverineModeControl.PcComboHint,
            _ => "Kol bulunamadı (kablo ya da alıcıyla bağlı ve açık olmalı)."
        };
        PadModeXboxBtn.IsEnabled = m == WolverineControllerMode.Pc && !_padModeBusy;
    }

    private async void PadModeRefresh_Click(object sender, RoutedEventArgs e) => await RefreshPadModeAsync();

    private void TriggerFeel_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string tag }) return;
        _settings.TriggerFeel = int.Parse(tag);
        _bridge.TriggerMotors = _settings.TriggerFeel == 1;
        try { _store.Save(_settings); } catch (Exception ex) { NoteError("Trigger feel", ex); }
        DiagnosticsService.Info("Trigger effects in Xbox mode: " + (_settings.TriggerFeel == 1 ? "trigger motors + haptics" : "classic (body motors)") + ".");
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
            UpdateBridgeStatus();
            _ = EnsureModeRunningAsync();
            return;
        }
        if (!VirtualDualSenseBridge.IsElevated)
        {
            var answer = System.Windows.MessageBox.Show(
                "Sanal DualSense oluşturmak yönetici izni ister. Uygulama yönetici olarak yeniden başlatılsın mı?",
                "Sanal DualSense köprüsü", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--start-virtual-dualsense") { UseShellExecute = true, Verb = "runas" });
                _realExit = true;
                System.Windows.Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception) { VcBridgeText.Text = "Yönetici izni verilmedi."; }
            return;
        }
        await StartVirtualBridgeAsync();
    }

    private async Task StartVirtualBridgeAsync()
    {
        if (_busy) return;
        StopAll();
        _busy = true;
        try
        {
            int slot = _settings.PreferredXInputSlot is >= 0 and <= 3 ? _settings.PreferredXInputSlot : 0;
            bool ok = await _bridge.StartVirtualDualSenseAsync(slot);
            if (ok) _run = RunState.Native;
        }
        catch (Exception ex) { NoteError("Virtual DualSense", ex); }
        finally { _busy = false; UpdateBridgeStatus(); }
    }

    private void UpdateBridgeStatus()
    {
        if (VcBridgeBtn == null) return;
        bool running = _bridge.IsVirtualDualSenseRunning;
        VcBridgeBtn.Content = running ? "Köprüyü durdur" : VirtualDualSenseBridge.IsElevated ? "Köprüyü başlat" : "Yönetici olarak başlat";
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

    private sealed record CheckResult(UsbGamepadPresence Usb, bool Synapse, bool Sdk, string? DualSenseEndpoint);

    private async Task RefreshChecksAsync()
    {
        CheckResult r = await Task.Run(() =>
        {
            var usb = UsbGamepadMonitor.Read();
            bool synapse = Process.GetProcessesByName("RazerAppEngine").Length > 0 ||
                           Process.GetProcessesByName("Razer Synapse").Length > 0;
            bool sdk = File.Exists(Path.Combine(AppContext.BaseDirectory, "HAR.dll")) &&
                       File.Exists(Path.Combine(AppContext.BaseDirectory, "Interhaptics.RazerProvider.dll"));
            string? endpoint = null;
            try { endpoint = NativeBridgeService.FindDualSenseEndpoint()?.Name; }
            catch (Exception ex) { DiagnosticsService.Error("Endpoint check", ex); }
            return new CheckResult(usb, synapse, sdk, endpoint);
        });

        SetCheck(DotWolverine, HintWolverine, r.Usb.WolverineWiredPcNode,
            "Kabloyla bağlı, PC modunda.",
            !r.Usb.ScanSucceeded ? "USB listesi okunamadı." :
            r.Usb.WolverineUsbNode ? "Sadece dongle / Xbox modu görünüyor. Kabloyla tak ve PC moduna al (○ + Menü + A, 2 sn)." :
            "Bulunamadı. Kabloyla tak ve PC moduna al (○ + Menü + A, 2 sn).");
        if (!r.Usb.WolverineWiredPcNode && r.Usb.WolverineUsbNode) DotWolverine.Fill = WarnBrush;
        SetCheck(DotSynapse, HintSynapse, r.Synapse,
            "Çalışıyor. Synapse'te Haptic Source: Sensa HD Games olmalı.",
            "Çalışmıyor. Razer Synapse 4'ü aç.");
        SetCheck(DotSdk, HintSdk, r.Sdk,
            "HAR.dll ve RazerProvider hazır.",
            "Eksik. HAR.dll ve Interhaptics.RazerProvider.dll uygulama klasörüne konmalı.");
        SetCheck(DotDualSense, HintDualSense, r.DualSenseEndpoint != null,
            "4 kanal ses aygıtı: " + r.DualSenseEndpoint,
            "4 kanallı DualSense ses aygıtı yok. DualSense'i USB ile tak veya sanal DualSense'i başlat.");
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

    private bool _starting;
    private WolverineMode _audioRoute = WolverineMode.Unknown;
    private AudioHapticsBridge? _xboxAudio;
    private long _nextAutoStartTick;
    private const int RetryMs = 5000;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        var mode = SelectedMode;
        if (_initialized && _run != RunState.Off) StopAll();
        _settings.Mode = mode;
        _settings.Route = HapticRouteCatalog.DefaultForMode(mode);
        _lastErrorNoted = null;
        _nextAutoStartTick = 0;
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
    private async Task EnsureModeRunningAsync()
    {
        if (_busy || _starting || _realExit || _run != RunState.Off) return;
        var mode = SelectedMode;
        if (mode == HapticMode.Normal || Environment.TickCount64 < _nextAutoStartTick) return;
        _starting = true;
        try
        {
            if (mode == HapticMode.Audio)
            {
                // Route by controller protocol: PC mode → Sensa, Xbox mode → motors + triggers.
                _audioRoute = await Task.Run(WolverineModeDetector.Detect);
                if (_audioRoute == WolverineMode.Pc)
                {
                    await _bridge.StartAudioToSensaAsync();
                    if (_busy || SelectedMode != HapticMode.Audio) { _bridge.Stop(); return; }
                }
                else if (_audioRoute == WolverineMode.Xbox)
                {
                    _xboxAudio = new AudioHapticsBridge(new XboxAudioSink { OutputLevelPercent = _settings.OutputLevelPercent });
                    _xboxAudio.Start();
                }
                else
                {
                    _rumble.StartAudio();          // unknown controller: classic XInput rumble
                }
                SetAudioPriority(true);
                _run = RunState.Audio;
            }
            else
            {
                await _bridge.StartBridgeAsync();
                if (_busy || _realExit || SelectedMode != HapticMode.Native) { _bridge.Stop(); return; }   // a test or mode change won
                _run = RunState.Native;
            }
            _lastErrorNoted = null;
            DiagnosticsService.Info("Mode " + mode + " running.");
        }
        catch (Exception ex)
        {
            _rumble.StopAudio();
            SetAudioPriority(false);
            _bridge.Stop();
            if (_lastErrorNoted != ex.Message) DiagnosticsService.Error(mode + " auto start", ex);
            _lastErrorNoted = ex.Message;
            _nextAutoStartTick = Environment.TickCount64 + RetryMs;
        }
        finally
        {
            _starting = false;
            RefreshStatus();
        }
    }

    private void StopAll()
    {
        _testCancel?.Cancel();
        _xboxAudio?.Dispose();
        _xboxAudio = null;
        _rumble.StopAudio();
        SetAudioPriority(false);
        _bridge.Stop();
        _run = RunState.Off;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _tickCount++;
            if (_tickCount % 8 == 0)
            {
                _performance.Sample();
                if (_run == RunState.Audio && _performance.SustainedHighSamples >= 3)
                {
                    StopAll();
                    _lastErrorNoted = "İşlemci sınırı (%3) üst üste aşıldı; sesten titreşim 1 dk durduruldu.";
                    _nextAutoStartTick = Environment.TickCount64 + 60_000;
                    DiagnosticsService.Info(_lastErrorNoted);
                }
            }
            bool audioAlive = _audioRoute switch
            {
                WolverineMode.Pc => _bridge.IsAudioToSensaRunning,
                WolverineMode.Xbox => _xboxAudio?.IsCapturing == true,
                _ => _rumble.Snapshot().IsCapturing
            };
            if (_run == RunState.Audio && !audioAlive)
            {
                _lastErrorNoted = (_audioRoute switch
                {
                    WolverineMode.Pc => _bridge.AudioFailure,
                    WolverineMode.Xbox => _xboxAudio?.Failure,
                    _ => _rumble.Snapshot().LastError
                }) ?? "Ses yakalama beklenmedik biçimde durdu.";
                StopAll();
                _nextAutoStartTick = Environment.TickCount64 + RetryMs;
            }
            if (_run == RunState.Native && !_busy && !_bridge.IsBridgeRunning && !_bridge.IsVirtualDualSenseRunning)
            {
                _lastErrorNoted = "Native köprü durdu: " + _bridge.SensaStatus + " / " + _bridge.SourceStatus;
                DiagnosticsService.Info(_lastErrorNoted);
                StopAll();
                _nextAutoStartTick = Environment.TickCount64 + RetryMs;
            }
            if (_tickCount % 4 == 0) _ = EnsureModeRunningAsync();   // auto-run / resume after tests
            if (TabSettings.IsChecked == true && _bridge.IsVirtualDualSenseRunning) UpdateBridgeStatus();
            RefreshStatus();
        }
        catch (Exception ex) { NoteError("Status refresh", ex); }
    }

    private void RefreshStatus()
    {
        var mode = SelectedMode;
        bool running = _run != RunState.Off;
        LevelPanel.Visibility = _run == RunState.Native ? Visibility.Visible : Visibility.Collapsed;
        string waitText = _busy ? "Test sürüyor; bitince otomatik devam eder."
            : _lastErrorNoted != null ? "Bekleniyor: " + _lastErrorNoted + " (birkaç saniyede bir yeniden denenir)"
            : "Başlatılıyor…";

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
                    WolverineMode.Xbox => "Xbox modu → ana motorlar + tetik motorları",
                    _ => "Klasik Xbox titreşimi"
                };
                bool sound = _audioRoute switch
                {
                    WolverineMode.Pc => _bridge.AudioHasRecentSound,
                    WolverineMode.Xbox => _xboxAudio?.HasRecentSound == true,
                    _ => snap.LastActiveSoundTick > 0 && Environment.TickCount64 - snap.LastActiveSoundTick < 3000
                };
                StatusTitle.Text = running ? "Sesten titreşim çalışıyor" : "Sesten titreşim";
                StatusDetail.Text = running
                    ? route + " • " + (sound ? "ses algılanıyor." : "şu an ses yok veya çok sessiz. Ses kaydedilmez.")
                    : waitText;
                ModeNote.Text = "Kolun modu otomatik algılanır: PC modunda Sensa, Xbox modunda ana motorlar + tetikler. " +
                                "Synapse'te 'Audio to Haptics' açıksa kapat; yoksa iki sistem aynı anda titretir.";
                SetBadge(running ? GoodBrush : WarnBrush, running ? "Çalışıyor" : "Bekliyor");
                break;
            default:
                if (running)
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
                    StatusTitle.Text = "Native DualSense (v2)";
                    StatusDetail.Text = waitText;
                    SetBadge(WarnBrush, "Bekliyor");
                }
                ModeNote.Text = "Deneysel, kayıplı v2 dönüşümü (33 ms kare). Oyun kaynağı ve fiziksel HD doğrulanmadı.";
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
        string? endpoint = await Task.Run(GameSignalSimulator.FindEndpoint);
        if (endpoint == null) { GameSimStatus.Text = "Sanal DualSense ses uç noktası bulunamadı."; return; }
        string? micId = ChannelMicEnabled.IsChecked == true && ChannelMicCombo.SelectedItem is LabMicrophone m ? m.Id : null;
        _gameSimCancel = new CancellationTokenSource();
        GameSimBtn.IsEnabled = false;
        var (h0, o0, t0) = _bridge.VirtualDualSenseCounters;
        long f0 = _bridge.DirectFramesSent;
        _bridge.MarkVirtualHapticPeaks();
        GameSimStatus.Text = "Çalıyor: " + scene.Title + " — " + scene.Hint;
        try
        {
            long x0 = _bridge.VirtualXboxMotorWrites;
            var res = await GameSignalSimulator.PlayAsync(scene.Id, endpoint, _bridge.VirtualDualSenseHidPath, micId, _gameSimCancel.Token);
            await Task.Delay(300);
            var (h1, o1, t1) = _bridge.VirtualDualSenseCounters;
            long frames = _bridge.DirectFramesSent - f0;
            var (pl, pr) = _bridge.VirtualHapticPeaks;
            bool xbox = _bridge.VirtualDualSenseMode == WolverineControllerMode.Xbox;
            GameSimStatus.Text = GameSimVerdict.Describe(h1 - h0, pl, pr, frames, res.ReportsWritten, t1 - t0, res.HidError, res.MicDb,
                xbox ? _bridge.VirtualXboxMotorWrites - x0 : null, xbox ? _bridge.VirtualXboxStatus : null);
            DiagnosticsService.Info("Game simulator " + scene.Id + ": " + GameSimStatus.Text.Replace(Environment.NewLine, " | ") + " | " + _bridge.SensaEngineSummary);
        }
        catch (OperationCanceledException) { GameSimStatus.Text = "Durduruldu."; }
        catch (Exception ex) { NoteError("Game simulator", ex); GameSimStatus.Text = "Hata: " + ex.Message; }
        finally { _gameSimCancel.Dispose(); _gameSimCancel = null; GameSimBtn.IsEnabled = true; }
    }

    private async void DemoSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _starting || DemoSceneCombo.SelectedItem is not ChannelLabScene { Demo: { } demo }) return;
        _busy = true;
        var token = BeginTest();
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
                bool ok = await _bridge.PlayDemoAsync(demo, token, progress);
                if (!ok) throw new InvalidOperationException(_bridge.SensaStatus);
                DemoStatus.Text = NativeDemoSignals.Title(demo) + " bitti • Wolverine v2 " +
                                  _bridge.EngineTickHz.ToString("F0") + " kare/sn";
            }
            DiagnosticsService.Info("Demo " + demo + " played on " + (DemoDevDualSense.IsChecked == true ? "DualSense" : "Wolverine v2") + ".");
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
                await _bridge.StartAudioToSensaAsync(token, null, 0, applySensitivity: false);
                route = "PC modu → Sensa motorları";
            }
            else if (mode == WolverineMode.Xbox)
            {
                xbox = new AudioHapticsBridge(new XboxAudioSink { OutputLevelPercent = _settings.OutputLevelPercent }) { DelayOverrideMs = 0 };
                xbox.Start();
                route = "Xbox modu → ana motorlar + tetikler";
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
                : "Xbox kontrolcüsü bulunamadı. Kolun bağlı ve PC modunda olduğunu kontrol et.";
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
