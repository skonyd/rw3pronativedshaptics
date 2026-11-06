using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WpfBrushes = System.Windows.Media.Brushes;
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
    private sealed record SensationChoice(PhysicalSensation Value, string Label);

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
        var sensationChoices = new[]
        {
            new SensationChoice(PhysicalSensation.NotEvaluated, "Henüz değerlendirilmedi"),
            new SensationChoice(PhysicalSensation.Felt, "✓ Evet, titreşimi hissettim"),
            new SensationChoice(PhysicalSensation.NotFelt, "✕ Hayır, titreşmedi"),
            new SensationChoice(PhysicalSensation.Uncertain, "? Emin değilim")
        };
        DsFeelingBox.ItemsSource = sensationChoices;
        RwFeelingBox.ItemsSource = sensationChoices;
        DsFeelingBox.SelectedIndex = RwFeelingBox.SelectedIndex = 0;
        SceneBox.ItemsSource = BehavioralHapticFixtures.All;
        TargetBox.SelectedIndex = 0;
        SceneBox.SelectedIndex = 0;
        SaveButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        RefreshUsb();
        RefreshGainLabel();
        UpdateTargetRatingControls();
        RefreshSavedSummary();
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
                    WpfBrushes.LightGreen : WpfBrushes.OrangeRed;
                DualSenseUsbDetail.Text = snapshot.DualSenseUsbNode
                    ? "Sony VID_054C • mevcut PnP kaydı (" + snapshot.DualSenseNodes + ")"
                    : "Kablo, USB portu ve sürücüyü kontrol et.";

                WolverineConnectedText.Text = snapshot.WolverineUsbNode
                    ? "✓ Wolverine V3 Pro USB kimliği" : "✕ Wolverine USB bulunamadı";
                WolverineConnectedText.Foreground = snapshot.WolverineUsbNode ?
                    WpfBrushes.LightGreen : WpfBrushes.OrangeRed;
                WolverineUsbDetail.Text = snapshot.WolverineUsbNode
                    ? "Razer VID_1532 PID_0A3F • dongle olabilir"
                    : "PC modu, USB kablosu ve aygıtı kontrol et.";
            }
            else
            {
                DualSenseConnectedText.Text = "○ DualSense • doğrulanamadı";
                WolverineConnectedText.Text = "○ Wolverine • doğrulanamadı";
                DualSenseConnectedText.Foreground = WolverineConnectedText.Foreground =
                    WpfBrushes.Goldenrod;
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
                    WpfBrushes.Goldenrod;
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
        ClearScoring();
    }

    private void Target_Changed(object sender, WpfControls.SelectionChangedEventArgs e)
    {
        ClearScoring();
        UpdateTargetRatingControls();
    }

    private void ClearScoring()
    {
        _lastResult = null;
        if (SaveButton != null) SaveButton.IsEnabled = false;
        if (_ratings != null) foreach (var r in _ratings) r.SelectedItem = null;
        if (DsFeelingBox != null) DsFeelingBox.SelectedIndex = 0;
        if (RwFeelingBox != null) RwFeelingBox.SelectedIndex = 0;
    }

    private ComparisonTarget SelectedTarget => TargetBox.SelectedIndex switch
    {
        1 => ComparisonTarget.DualSenseOnly,
        2 => ComparisonTarget.WolverineOnly,
        _ => ComparisonTarget.Both
    };

    private void UpdateTargetRatingControls()
    {
        if (DsFeelingBox == null || _ratings == null) return;
        bool ds = BehavioralEvidenceRules.IncludesDualSense(SelectedTarget);
        bool rw = BehavioralEvidenceRules.IncludesWolverine(SelectedTarget);
        DsFeelingBox.IsEnabled = ds;
        RwFeelingBox.IsEnabled = rw;
        DsTexture.IsEnabled = DsImpact.IsEnabled = DsStereo.IsEnabled = ds;
        RwTexture.IsEnabled = RwImpact.IsEnabled = RwStereo.IsEnabled = rw;
        if (!ds) DsFeelingBox.SelectedIndex = 0;
        if (!rw) RwFeelingBox.SelectedIndex = 0;
    }

    private void SetTestControlsEnabled(bool enabled)
    {
        SceneBox.IsEnabled = TargetBox.IsEnabled = UsbEndpointBox.IsEnabled = enabled;
        CheckConnectionsButton.IsEnabled = RefreshUsbButton.IsEnabled = enabled;
        ExclusiveCheck.IsEnabled = ConfirmUsbCheck.IsEnabled = ConfirmSensaCheck.IsEnabled = enabled;
        WiredWolverineConfirmed.IsEnabled = WolverineLevel.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled && !_waitingToClose;
        if (enabled) UpdateTargetRatingControls();
        else
        {
            DsFeelingBox.IsEnabled = RwFeelingBox.IsEnabled = false;
            foreach (var rating in _ratings) rating.IsEnabled = false;
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_waitingToClose) _activePlaybackTask = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_busy || _waitingToClose || SceneBox.SelectedItem is not BehavioralScene scene) return;
        ComparisonTarget target = SelectedTarget;
        _playingTarget = target;
        bool usb = BehavioralEvidenceRules.IncludesDualSense(target);
        bool razer = BehavioralEvidenceRules.IncludesWolverine(target);
        var selected = UsbEndpointBox.SelectedItem as UsbOption;
        _busy = true;
        SetTestControlsEnabled(false);
        StopButton.IsEnabled = true;
        using var cts = new CancellationTokenSource();
        _running = cts;
        try
        {
            // Do not trust a cached scan: a device might have unplugged since the
            // green check last appeared. WMI runs off UI thread.
            UsbGamepadPresence fresh = await Task.Run(UsbGamepadMonitor.Read);
            cts.Token.ThrowIfCancellationRequested();
            if (!fresh.ScanSucceeded)
            {
                System.Windows.MessageBox.Show(this,
                    "Windows USB PnP durumu okunamadı; fiziksel A/B testini doğrulayamıyoruz: " +
                    fresh.Diagnostic, "USB bağlantı kontrolü", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            if (usb && !fresh.DualSenseUsbNode)
            {
                System.Windows.MessageBox.Show(this,
                    "Sony DualSense USB kimliği algılanmadı. Kabloyla bağla ve yeniden kontrol et.",
                    "DualSense USB bağlı değil", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (razer && (!fresh.WolverineUsbNode ||
                WiredWolverineConfirmed.IsChecked != true))
            {
                System.Windows.MessageBox.Show(this,
                    "Razer Wolverine V3 Pro USB kimliği ve doğrudan kablo bağlantısı gerekli. " +
                    "Kablosuz USB alıcısı aynı kimliği kullanabilir.",
                    "Wolverine USB onayı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (usb)
            {
                if (selected is null || !selected.Compatible || ConfirmUsbCheck.IsChecked != true)
                {
                    System.Windows.MessageBox.Show(this,
                        "Gerçek USB DualSense 4ch/48kHz ses çıkışını seçip fiziksel aygıtı doğrula.",
                        "USB DualSense aygıt seçimi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!IsLikelyPhysicalDualSense(selected.FriendlyName) &&
                    System.Windows.MessageBox.Show(this,
                        "Ses aygıtı adı fiziksel DualSense'e benzemiyor: " +
                        selected.FriendlyName + ". Devam etmek istediğine emin misin?",
                        "USB ses aygıtını kontrol et", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            if (razer && ConfirmSensaCheck.IsChecked != true)
            {
                System.Windows.MessageBox.Show(this,
                    "Razer SDK ve fiziksel Wolverine haptic testine izin vermelisin.",
                    "Sensa izin/onay", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (System.Windows.MessageBox.Show(this,
                scene.Name + " kısa, SENTETİK referans titreşimi oynatacak. " +
                "USB DualSense orijinal RW3 PCM tamponunu yazılımda değiştirmeden alacak. " +
                "Wolverine kayıplı parametrik çıkış kullanacak. Başlat?",
                "A/B fiziksel test onayı", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;
            cts.Token.ThrowIfCancellationRequested();

            _playingTarget = target;
            _lastUsbName = selected?.FriendlyName;
            _lastGlobalLevelPercent = _globalLevelPercent;
            _lastSceneLevelPercent = HapticOutputLevel.Limit(
                (int)Math.Round(WolverineLevel.Value));
            ClearScoring();
            PlaybackStatus.Text = scene.Name + " test ediliyor: " +
                (usb ? "DualSense değişmeyen PCM • " : "") +
                (razer ? "Wolverine kayıplı Sensa" : "");
            _sensa.OutputLevelPercent = _globalLevelPercent;
            ComparisonResult result = await BehavioralComparisonPlayer.PlayAsync(
                scene.Id, target, selected?.Id, ExclusiveCheck.IsChecked == true,
                _lastSceneLevelPercent, _sensa, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _lastResult = result;
            PlaybackStatus.Text = "Oynatma yazılımı tamamlandı. USB PCM gönderildi=" +
                result.UsbPcmSubmitted + ", Sensa render gözlendi=" +
                result.RazerProviderTicked +
                ". Kol hareketi otomatik ölçülmedi; aşağıda ayrı ayrı bildir.";
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            PlaybackStatus.Text = "Test iptal edildi; çıkış kapatıldı. Puan kaydı açılmadı.";
        }
        catch (Exception ex)
        {
            PlaybackStatus.Text = "A/B sahne hatası: " + ex.Message;
            DiagnosticsService.Error("Behavioral A/B scene " + scene.Id, ex);
        }
        finally
        {
            _sensa.Submit(0, 0);
            _sensa.Stop();
            if (ReferenceEquals(_running, cts)) _running = null;
            _busy = false;
            StopButton.IsEnabled = false;
            SetTestControlsEnabled(true);
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

    private static PhysicalSensation GetFeeling(WpfControls.ComboBox box) =>
        box.SelectedItem is SensationChoice choice
            ? choice.Value : PhysicalSensation.NotEvaluated;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastResult == null) return;
        var ds = new TactileRatings(GetRating(DsTexture),
            GetRating(DsImpact), GetRating(DsStereo));
        var rw = new TactileRatings(GetRating(RwTexture),
            GetRating(RwImpact), GetRating(RwStereo));
        PhysicalSensation dsFelt = GetFeeling(DsFeelingBox);
        PhysicalSensation rwFelt = GetFeeling(RwFeelingBox);
        if (!BehavioralEvidenceRules.CanSave(_lastResult.Target,
            dsFelt, rwFelt, ds, rw))
        {
            SaveStatus.Text = "Her oynatılan kol için 'Hissettim', 'Hissetmedim' veya " +
                "'Emin değilim' seç. Hissettim ise üç puanı da doldur. " +
                "Test edilmeyen kola his/puan yazma.";
            return;
        }
        try
        {
            string directory = ResultsDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "behavioral-ab-scores.jsonl");
            var entry = new BehavioralEvidenceEntry(
                BehavioralEvidenceEntry.Schema,
                DateTimeOffset.UtcNow,
                _lastResult.Scene, _lastResult.Target,
                _lastResult.UsbPcmSubmitted, _lastResult.RazerProviderTicked,
                _lastResult.ExclusiveUsbMode, _lastResult.UsbPcmSubmitted ? _lastUsbName : null,
                _lastGlobalLevelPercent, _lastSceneLevelPercent,
                dsFelt, rwFelt, ds, rw,
                NotesBox.Text);
            if (!entry.Valid)
                throw new InvalidOperationException("Fiziksel deneyim puanları doğrulanamadı.");
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            SaveStatus.Text = "Fiziksel his gözlemi yalnızca yerel dosyaya kaydedildi: " + path;
            RefreshSavedSummary();
            DiagnosticsService.Info("User-reported A/B sensation recorded for " + _lastResult.Scene);
            SaveButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Puan kaydı başarısız: " + ex.Message;
            DiagnosticsService.Error("Save A/B score", ex);
        }
    }

    private void RefreshSavedSummary()
    {
        if (SummaryText == null) return;
        try
        {
            string path = Path.Combine(ResultsDirectory(), "behavioral-ab-scores.jsonl");
            if (!File.Exists(path))
            {
                SummaryText.Text = BehavioralEvidenceReport.Summarize(
                    Array.Empty<BehavioralEvidenceEntry>()).Display;
                return;
            }
            var entries = new System.Collections.Generic.List<BehavioralEvidenceEntry>();
            foreach (string line in File.ReadLines(path))
            {
                try
                {
                    var report = JsonSerializer.Deserialize<BehavioralEvidenceEntry>(line);
                    if (report?.Valid == true) entries.Add(report);
                }
                catch (JsonException) { /* Older schema is ignored rather than guessed. */ }
            }
            SummaryText.Text = BehavioralEvidenceReport.Summarize(entries).Display;
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Yerel rapor okunamadı: " + ex.Message;
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
        _usbClosing = true;
        _usbTimer.Stop();
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
