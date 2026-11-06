using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WpfControls = System.Windows.Controls;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// A/B lab: synthetic reference -> unmodified USB DualSense PCM,
/// plus a lossy Interhaptics envelope on Wolverine.
/// Hardware success must be confirmed by the person holding the controllers.
/// </summary>
public partial class BehavioralComparisonWindow : Window
{
    private sealed record UsbOption(string Id, string Display, string FriendlyName, bool Compatible);

    private readonly SensaStereoPrototype _sensa = new();
    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private UsbGamepadPresence _usbPresence = UsbGamepadPresence.Unknown("Henüz taranmadı");
    private int _usbScanInProgress;
    private bool _usbClosing;
    private readonly WpfControls.ComboBox[] _ratings;
    private CancellationTokenSource? _running;
    private ComparisonResult? _lastResult;
    private string? _lastUsbName;
    private int _globalLevelPercent;
    private int _lastGlobalLevelPercent;
    private int _lastSceneLevelPercent;
    private ComparisonTarget _playingTarget = ComparisonTarget.Both;
    private bool _busy;
    private Task? _activePlaybackTask;
    private bool _waitingToClose;
    private bool _safeToClose;

    public BehavioralComparisonWindow(int globalLevelPercent)
    {
        InitializeComponent();
        _globalLevelPercent = HapticOutputLevel.Limit(globalLevelPercent);
        _sensa.OutputLevelPercent = _globalLevelPercent;
        _ratings = new[] { DsTexture, RwTexture, DsImpact, RwImpact, DsStereo, RwStereo };
        foreach (var box in _ratings)
            box.ItemsSource = Enumerable.Range(1, 5).ToArray();
        SceneBox.ItemsSource = BehavioralHapticFixtures.All;
        TargetBox.SelectedIndex = 0;
        SceneBox.SelectedIndex = 0;
        SaveButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        RefreshUsb();
        RefreshGainLabel();
        _usbTimer.Tick += (_, _) => _ = RefreshConnectionsAsync();
        _usbTimer.Start();
        _ = RefreshConnectionsAsync();
    }

    public bool IsPlaying => _busy || _activePlaybackTask?.IsCompleted == false;

    private void CheckConnections_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshConnectionsAsync();
        RefreshUsb();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (_usbClosing || Interlocked.Exchange(ref _usbScanInProgress, 1) == 1) return;
        try
        {
            UsbGamepadPresence snapshot = await Task.Run(UsbGamepadMonitor.Read);
            if (_usbClosing || !IsLoaded) return;
            var previous = _usbPresence;
            _usbPresence = snapshot;
            if (snapshot.ScanSucceeded)
            {
                DualSenseConnectedText.Text = snapshot.DualSenseUsbNode
                    ? "✓ DualSense USB algılandı" : "✕ DualSense USB bulunamadı";
                DualSenseConnectedText.Foreground = snapshot.DualSenseUsbNode ?
                    Brushes.LightGreen : Brushes.OrangeRed;
                DualSenseUsbDetail.Text = snapshot.DualSenseUsbNode
                    ? "Sony VID_054C • mevcut PnP kaydı (" + snapshot.DualSenseNodes + ")"
                    : "Kablo, USB portu ve sürücüyü kontrol et.";

                WolverineConnectedText.Text = snapshot.WolverineUsbNode
                    ? "✓ Wolverine V3 Pro USB kimliği" : "✕ Wolverine USB bulunamadı";
                WolverineConnectedText.Foreground = snapshot.WolverineUsbNode ?
                    Brushes.LightGreen : Brushes.OrangeRed;
                WolverineUsbDetail.Text = snapshot.WolverineUsbNode
                    ? "Razer VID_1532 PID_0A3F • dongle olabilir"
                    : "PC modu, USB kablosu ve aygıtı kontrol et.";
            }
            else
            {
                DualSenseConnectedText.Text = "○ DualSense • doğrulanamadı";
                WolverineConnectedText.Text = "○ Wolverine • doğrulanamadı";
                DualSenseConnectedText.Foreground = WolverineConnectedText.Foreground =
                    Brushes.Goldenrod;
                DualSenseUsbDetail.Text = WolverineUsbDetail.Text = snapshot.Diagnostic;
            }
            if (previous.ScanSucceeded && snapshot.ScanSucceeded)
            {
                if (!snapshot.DualSenseUsbNode) ConfirmUsbCheck.IsChecked = false;
                if (!snapshot.WolverineUsbNode) WiredWolverineConfirmed.IsChecked = false;
                // Stopping on disappearing USB roots prevents continued A/B
                // output after unplug or switching devices.
                if (IsPlaying && ((!snapshot.DualSenseUsbNode &&
                        _playingTarget is ComparisonTarget.Both or ComparisonTarget.DualSenseOnly)
                    || (!snapshot.WolverineUsbNode &&
                        _playingTarget is ComparisonTarget.Both or ComparisonTarget.WolverineOnly)))
                {
                    RequestStop();
                    PlaybackStatus.Text = "USB aygıt bağlantısı kayboldu; güvenlik için A/B testi durduruluyor.";
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("A/B USB presence monitor", ex);
            if (!_usbClosing)
            {
                _usbPresence = UsbGamepadPresence.Unknown(ex.Message);
                DualSenseConnectedText.Text = WolverineConnectedText.Text = "○ USB durumu doğrulanamadı";
                DualSenseConnectedText.Foreground = WolverineConnectedText.Foreground =
                    Brushes.Goldenrod;
                DualSenseUsbDetail.Text = WolverineUsbDetail.Text = ex.Message;
            }
        }
        finally { Interlocked.Exchange(ref _usbScanInProgress, 0); }
    }

    public void RequestStop()
    {
        _running?.Cancel();
        _sensa.Submit(0, 0);
    }

    public async Task StopAndCloseAsync()
    {
        RequestStop();
        if (_activePlaybackTask != null)
        {
            try { await _activePlaybackTask; }
            catch (Exception ex) { DiagnosticsService.Error("A/B shutdown", ex); }
        }
        if (IsLoaded)
        {
            _safeToClose = true;
            Close();
        }
    }

    public void UpdateOutputLevel(int level)
    {
        _globalLevelPercent = HapticOutputLevel.Limit(level);
        _sensa.OutputLevelPercent = _globalLevelPercent;
        RefreshGainLabel();
    }

    private void RefreshGainLabel()
    {
        if (WolverineLevelLabel == null) return;
        int test = HapticOutputLevel.Limit((int)Math.Round(WolverineLevel.Value));
        WolverineLevelLabel.Text = "Wolverine karşılaştırma seviyesi: %" + test +
            " × RW3 genel %" + _globalLevelPercent +
            " = yaklaşık %" + (test * _globalLevelPercent / 100) +
            " | DualSense referansı değişmez.";
    }

    private void WolverineLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WolverineLevelLabel != null) RefreshGainLabel();
    }

    private void RefreshUsb_Click(object sender, RoutedEventArgs e) => RefreshUsb();

    private void RefreshUsb()
    {
        var old = (UsbEndpointBox.SelectedItem as UsbOption)?.Id;
        UsbEndpointBox.Items.Clear();
        ConfirmUsbCheck.IsChecked = false;
        try
        {
            foreach (var ep in NativeDualSenseCapture.EnumeratePossibleSources())
            {
                UsbEndpointBox.Items.Add(new UsbOption(ep.Id,
                    ep.Name + " • " + ep.Channels + "ch / " + ep.SampleRate + "Hz / " +
                    ep.BitsPerSample + "bit " + (ep.CanCapture ? "• 4ch adayı" : "• UYUMSUZ"),
                    ep.Name, ep.CanCapture));
            }
            if (old != null)
                foreach (UsbOption opt in UsbEndpointBox.Items)
                    if (opt.Id == old) { UsbEndpointBox.SelectedItem = opt; break; }
            if (UsbEndpointBox.SelectedItem == null)
                foreach (UsbOption opt in UsbEndpointBox.Items)
                    if (opt.Compatible && IsLikelyPhysicalDualSense(opt.FriendlyName))
                    { UsbEndpointBox.SelectedItem = opt; break; }
        }
        catch (Exception ex)
        {
            PlaybackStatus.Text = "Ses aygıtları okunamadı: " + ex.Message;
            DiagnosticsService.Error("A/B USB endpoint list", ex);
        }
    }

    private static bool IsLikelyPhysicalDualSense(string name)
    {
        string s = name.ToLowerInvariant();
        if (s.Contains("virtual") || s.Contains("padforge") ||
            s.Contains("dsx") || s.Contains("vigem") || s.Contains("hidmaestro"))
            return false;
        return s.Contains("dualsense") || s.Contains("wireless controller");
    }

    private void UsbEndpoint_Changed(object sender, WpfControls.SelectionChangedEventArgs e)
    {
        if (ConfirmUsbCheck != null) ConfirmUsbCheck.IsChecked = false;
    }

    private void Scene_Changed(object sender, WpfControls.SelectionChangedEventArgs e)
    {
        if (SceneBox.SelectedItem is BehavioralScene scene && SceneDetails != null)
            SceneDetails.Text = scene.Feel + " • " + scene.DurationMs +
                " ms • sentetik referans, gerçek oyun kayıtlarından üretilmedi.";
        _lastResult = null;
        if (SaveButton != null) SaveButton.IsEnabled = false;
        if (_ratings != null) foreach (var r in _ratings) r.SelectedItem = null;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_waitingToClose) _activePlaybackTask = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_busy || SceneBox.SelectedItem is not BehavioralScene scene) return;
        ComparisonTarget target = TargetBox.SelectedIndex switch
        {
            1 => ComparisonTarget.DualSenseOnly,
            2 => ComparisonTarget.WolverineOnly,
            _ => ComparisonTarget.Both
        };
        bool usb = target is ComparisonTarget.DualSenseOnly or ComparisonTarget.Both;
        bool razer = target is ComparisonTarget.WolverineOnly or ComparisonTarget.Both;
        var selected = UsbEndpointBox.SelectedItem as UsbOption;
        await RefreshConnectionsAsync();
        if (!_usbPresence.ScanSucceeded)
        {
            System.Windows.MessageBox.Show(this,
                "Windows USB PnP durumu okunamadı. İki fiziksel kolu güvenle doğrulamadan test başlatılamaz. " +
                _usbPresence.Diagnostic, "Bağlantı kontrolü eksik",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (usb && !_usbPresence.DualSenseUsbNode)
        {
            System.Windows.MessageBox.Show(this,
                "Sony DualSense USB kimliği algılanmadı. Gerçek DualSense'i kabloyla bağla ve yenile.",
                "USB DualSense bağlı değil", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (razer && (!_usbPresence.WolverineUsbNode ||
            WiredWolverineConfirmed.IsChecked != true))
        {
            System.Windows.MessageBox.Show(this,
                "Razer Wolverine V3 Pro USB kimliğinin algılanması ve doğrudan kablo " +
                "bağlantısının kullanıcı tarafından doğrulanması gerekiyor. Kablosuz alıcı tek başına yeterli değil.",
                "Wolverine kablo doğrulaması", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (usb)
        {
            if (selected is null || !selected.Compatible || ConfirmUsbCheck.IsChecked != true)
            {
                System.Windows.MessageBox.Show(this,
                    "Gerçek USB DualSense'in 4ch/48k ses çıkışını seç ve fiziksel aygıtı onayla.",
                    "DualSense aygıtı doğrulanmadı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!IsLikelyPhysicalDualSense(selected.FriendlyName) &&
                System.Windows.MessageBox.Show(this,
                    "Aygıt adı fiziksel DualSense'e benzemiyor: " + selected.FriendlyName +
                    ". Yanlış aygıta sentetik ses göndermek istemiyorsan iptal et. Devam?",
                    "USB aygıt kimliğini kontrol et", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;
        }
        if (razer && ConfirmSensaCheck.IsChecked != true)
        {
            System.Windows.MessageBox.Show(this,
                "Önce Wolverine'ın PC modunda olduğunu doğrulayıp Razer Sensa sentetik motor testine izin ver.",
                "Wolverine haptics onayı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (System.Windows.MessageBox.Show(this,
            scene.Name + " sahnesi fiziksel kontrolcülerde kısa titreşimler oluşturabilir. " +
            "DualSense'e yazılım kazancı uygulanmadan sentetik 4ch/48k PCM verilir. " +
            "Wolverine'a ise kayıplı Sensa parametrik gösterimi uygulanır. Başlat?",
            "Fiziksel A/B testi", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _busy = true;
        _playingTarget = target;
        _lastResult = null;
        _lastUsbName = selected?.FriendlyName;
        _lastGlobalLevelPercent = _globalLevelPercent;
        _lastSceneLevelPercent = HapticOutputLevel.Limit((int)Math.Round(WolverineLevel.Value));
        SaveButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        foreach (var rating in _ratings) rating.SelectedItem = null;
        using var cts = new CancellationTokenSource();
        _running = cts;
        try
        {
            PlaybackStatus.Text = scene.Name + " çalışıyor... " +
                (usb ? "DualSense: değişmeyen PCM • " : "") +
                (razer ? "Wolverine: kayıplı Sensa" : "");
            _sensa.OutputLevelPercent = _globalLevelPercent;
            _lastResult = await BehavioralComparisonPlayer.PlayAsync(
                scene.Id, target, selected?.Id, ExclusiveCheck.IsChecked == true,
                _lastSceneLevelPercent, _sensa, cts.Token);
            PlaybackStatus.Text = "Oynatma rutini tamamlandı. USB PCM gönderimi=" +
                _lastResult.UsbPcmSubmitted + ", Razer provider döngüsü=" +
                _lastResult.RazerProviderTicked + ". Fiziksel his ve doğruluk için puan ver.";
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            PlaybackStatus.Text = "Test iptal edildi; motor sıfırlandı.";
        }
        catch (Exception ex)
        {
            PlaybackStatus.Text = "A/B test hatası: " + ex.Message;
            DiagnosticsService.Error("Behavioral A/B scene " + scene.Id, ex);
        }
        finally
        {
            _sensa.Submit(0, 0);
            // Do not keep provider effects alive between separate test scenes.
            _sensa.Stop();
            if (ReferenceEquals(_running, cts)) _running = null;
            _busy = false;
            StopButton.IsEnabled = false;
            PlayButton.IsEnabled = true;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _running?.Cancel();
        _sensa.Submit(0, 0);
        PlaybackStatus.Text = "Durdurma istendi; tüm etkin test görevleri iptal ediliyor.";
    }

    private static int? GetRating(WpfControls.ComboBox box) =>
        box.SelectedItem is int i ? i : null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult == null) return;
        int?[] ds = { GetRating(DsTexture), GetRating(DsImpact), GetRating(DsStereo) };
        int?[] rw = { GetRating(RwTexture), GetRating(RwImpact), GetRating(RwStereo) };
        if (!ds.Concat(rw).Any(x => x.HasValue))
        {
            SaveStatus.Text = "En az bir fiziksel his puanı seç.";
            return;
        }
        try
        {
            string directory = ResultsDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "behavioral-ab-scores.jsonl");
            var item = new
            {
                Utc = DateTimeOffset.UtcNow,
                Scene = _lastResult.Scene.ToString(),
                Target = _lastResult.Target.ToString(),
                UsbEndpoint = _lastResult.UsbPcmSubmitted ? _lastUsbName : null,
                UsbWasapiExclusive = _lastResult.ExclusiveUsbMode,
                UsbPcmSubmitted = _lastResult.UsbPcmSubmitted,
                RazerProviderRenderTicked = _lastResult.RazerProviderTicked,
                SyntheticNotGameAuthored = true,
                RazerOutputLossy50Hz = true,
                RealWorldPhysicalSensationNotMachineVerified = true,
                RwGlobalLevelPercent = _lastGlobalLevelPercent,
                RwScenarioLevelPercent = _lastSceneLevelPercent,
                DsTexture = ds[0], DsImpact = ds[1], DsStereo = ds[2],
                RwTexture = rw[0], RwImpact = rw[1], RwStereo = rw[2],
                Notes = NotesBox.Text
            };
            File.AppendAllText(path, JsonSerializer.Serialize(item) + Environment.NewLine);
            SaveStatus.Text = "Sahne değerlendirmesi yerel dosyaya kaydedildi: " + path;
            DiagnosticsService.Info("Local A/B subjective score saved for " + _lastResult.Scene);
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Kayıt başarısız: " + ex.Message;
            DiagnosticsService.Error("Save A/B score", ex);
        }
    }

    private static string ResultsDirectory() =>
        Path.Combine(Path.GetDirectoryName(new SettingsStore().FilePath)!,
            "haptic-comparison");

    private void OpenResults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = ResultsDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory, UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Kayıt dizini açılamadı: " + ex.Message;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_safeToClose || (!_busy && _activePlaybackTask?.IsCompleted != false))
        {
            _usbClosing = true;
            _usbTimer.Stop();
            // No pending async work: do not call Close() from inside Closing.
            _safeToClose = true;
            _sensa.Submit(0, 0);
            _sensa.Dispose();
            return;
        }

        // Cancel the pending close, wait for both WASAPI and provider loops to
        // stop, then issue a *new* Close after the async work has completed.
        e.Cancel = true;
        if (_waitingToClose) return;
        _waitingToClose = true;
        PlayButton.IsEnabled = false;
        RequestStop();
        if (_activePlaybackTask != null)
        {
            try { await _activePlaybackTask; }
            catch (Exception ex) { DiagnosticsService.Error("A/B shutdown wait", ex); }
        }
        _safeToClose = true;
        Close();
    }
}
