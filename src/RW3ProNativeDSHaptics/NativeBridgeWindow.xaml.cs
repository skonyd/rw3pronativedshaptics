using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RW3ProNativeDSHaptics;

public partial class NativeBridgeWindow : Window
{
    private sealed record EndpointOption(string Id, string Display, bool CanCapture, string FriendlyName);
    private readonly HapticRoute _route;
    private readonly SensaStereoPrototype _sensa = new();
    private readonly NativeDualSenseCapture _source = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    private enum SessionMode { Off, SourceOnly, SensaBridge }
    private SessionMode _session = SessionMode.Off;
    private int _cancelGeneration;
    private bool _leftPhysical, _rightPhysical;
    public string StatusForParent =>
        "Kaynak tercihi=" + _route +
        " | Oturum=" + _session +
        " | Kaynak durumu=" + NativeCaptureHealth.Describe(_source.CurrentState) +
        " | UAC haptic 3./4. kanal etkin=" + _source.IsCapturing +
        " | alınan PCM çerçevesi=" + _source.CapturedFrames +
        " | sol haptic blok=" + _source.LeftActiveBlocks +
        " | sağ haptic blok=" + _source.RightActiveBlocks +
        " | speaker RMS=" + _source.SpeakerRms.ToString("F3") +
        " | rear L/R RMS=" + _source.LeftRms.ToString("F3") + "/" + _source.RightRms.ToString("F3") +
        " | Razer provider başlatıldı=" + _sensa.IsActive +
        " | parametric render tick=" + _sensa.EngineTicks +
        " | fiziksel teyit (sol/sağ)=" + _leftPhysical + "/" + _rightPhysical +
        ". Gerçek oyun kaynağı ve bit-perfect aktarım DOĞRULANMADI.";

    public bool HasRecentHapticChannelSamples =>
        HapticSafetyRules.HasRecentChannelEnergy(
            _source.IsCapturing, _source.LastLeftEnergyTick,
            _source.LastRightEnergyTick, Environment.TickCount64);


    public NativeBridgeWindow(HapticRoute route = HapticRoute.NativeManualUac)
    {
        _route = HapticRouteCatalog.IsNative(route) ? route : HapticRoute.NativeManualUac;
        InitializeComponent();
        var info = HapticRouteCatalog.Get(_route);
        SelectedRouteText.Text = "Kaynak tercihi: " + info.DisplayName;
        RouteInstructionsText.Text = info.Guidance +
            " RW3 haricî sanal kontrolcü yazılımını veya sürücülerini kurmaz ve başlatmaz.";
        RefreshSources();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private void RefreshSources()
    {
        string? old = (EndpointPicker.SelectedItem as EndpointOption)?.Id;
        EndpointPicker.Items.Clear();
        EndpointAcknowledgement.IsChecked = false;
        try
        {
            foreach (var item in NativeDualSenseCapture.EnumeratePossibleSources())
            {
                // The user must select a real 4-ch DualSense USB/UAC output.
                EndpointPicker.Items.Add(new EndpointOption(item.Id,
                    item.Name + " | " + item.Channels + "ch/" + item.SampleRate + "Hz/" +
                    item.BitsPerSample + "bit | " +
                    (item.CanCapture ? "UAC format adayı (kimlik doğrulanmalı)" : "UYUMSUZ FORMAT"),
                    item.CanCapture, item.Name));
            }
            foreach (EndpointOption entry in EndpointPicker.Items)
                if (entry.Id == old) { EndpointPicker.SelectedItem = entry; break; }
            if (EndpointPicker.SelectedItem == null)
            {
                // A friendly-name match is advisory, never a source identity certificate.
                foreach (EndpointOption entry in EndpointPicker.Items)
                    if (entry.CanCapture && HapticRouteCatalog.NameMayMatch(_route, entry.FriendlyName))
                    { EndpointPicker.SelectedItem = entry; break; }
                if (EndpointPicker.SelectedItem == null)
                    foreach (EndpointOption entry in EndpointPicker.Items)
                        if (entry.CanCapture) { EndpointPicker.SelectedItem = entry; break; }
                if (EndpointPicker.SelectedItem == null && EndpointPicker.Items.Count > 0)
                    EndpointPicker.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Native UAC enumerate", ex);
            SourceStateText.Text = "Aygıt listesi okunamadı: " + ex.Message;
        }
    }

    private void EndpointPicker_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // An acknowledgement applies only to the *specific* selected endpoint.
        EndpointAcknowledgement.IsChecked = false;
        if (_source.IsCapturing)
        {
            Interlocked.Increment(ref _cancelGeneration);
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            RefreshStatus();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private EndpointOption? GetApprovedEndpoint()
    {
        if (EndpointPicker.SelectedItem is not EndpointOption selected)
        {
            System.Windows.MessageBox.Show(this,
                "Lütfen dört kanallı DualSense UAC ses aygıtını seç.", "Ses kaynağı gerekli");
            return null;
        }
        if (!HapticRouteCatalog.CanStartBridge(_route, selected.CanCapture,
            EndpointAcknowledgement.IsChecked == true))
        {
            System.Windows.MessageBox.Show(this,
                "4ch/48k PCM16/Float32 aygıtı seçip aygıt/kaynak eşlemesini kontrol ettiğini " +
                "işaretlemeden izleme veya aktarım başlatılamaz.",
                "Kaynak doğrulaması gerekli", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (!HapticRouteCatalog.NameMayMatch(_route, selected.FriendlyName) &&
            System.Windows.MessageBox.Show(this,
                "Aygıt adı (" + selected.FriendlyName +
                ") seçili yönteme benzemiyor. Ad sadece ipucudur, gerçek kaynak kanıtı değildir. Devam?",
                "Kaynak adını kontrol et", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
            return null;
        return selected;
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        EndpointOption? selected = GetApprovedEndpoint();
        if (selected == null) return;
        if (System.Windows.MessageBox.Show(this,
            "Kaynağın 4 kanallı PCM akışı yalnızca yerel olarak izlenecek. " +
            "Razer DLL'i başlatılmayacak, motorlara komut verilmeyecek ve PCM dosyaya kaydedilmeyecek. " +
            "Bu veri oyun kaynaklı olduğunu otomatik kanıtlamaz. Devam?",
            "Kaynak-only gözlem", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        _busy = true;
        MonitorButton.IsEnabled = false;
        try
        {
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            _source.Start(selected.Id);
            _session = SessionMode.SourceOnly;
            SourceProbeText.Text = "UAC kaynak-only izleme başladı. Stereo kaynak testi için sol/sağ düğmesini kullan.";
            DiagnosticsService.Info("Source-only 4ch UAC started: " + selected.FriendlyName +
                "; no vendor DLLs, no motor output.");
        }
        catch (Exception ex)
        {
            _session = SessionMode.Off;
            _source.Stop();
            DiagnosticsService.Error("Source-only monitor", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "Kaynak izleme başlatılamadı",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            MonitorButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        EndpointOption? selected = GetApprovedEndpoint();
        if (selected == null) return;
        if (System.Windows.MessageBox.Show(this,
            "Dört kanallı DualSense UAC sesinin arka 3./4. kanalları iki ayrı Razer Sensa " +
            "parametrik efektine KAYIPLI dönüştürülecek. Bit-perfect aktarım ve fiziksel " +
            "sol/sağ motor eşlemesi doğrulanmadı. Devam?",
            "Deneysel Razer köprüsü", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        _busy = true;
        StartButton.IsEnabled = false;
        MonitorButton.IsEnabled = false;
        EndpointPicker.IsEnabled = false;
        int generation = Volatile.Read(ref _cancelGeneration);
        try
        {
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            if (!_sensa.Start())
                throw new InvalidOperationException(_sensa.Status);
            for (int i = 0; i < 25 && !_sensa.IsActive && _sensa.IsRunning; i++)
                await Task.Delay(80, _lifetime.Token);
            if (!_sensa.IsActive)
                throw new InvalidOperationException(_sensa.Status);
            if (generation != Volatile.Read(ref _cancelGeneration))
                throw new OperationCanceledException("Başlatma kullanıcı tarafından durduruldu.");
            _source.Start(selected.Id, _sensa);
            _session = SessionMode.SensaBridge;
            DiagnosticsService.Info("Experimental Sensa bridge route=" + _route +
                ", endpoint=" + selected.FriendlyName +
                "; 50Hz parametric stereo envelopes, no raw PCM fidelity assertion.");
        }
        catch (Exception ex)
        {
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            DiagnosticsService.Error("Native bridge start", ex);
            if (ex is not OperationCanceledException)
                System.Windows.MessageBox.Show(this, ex.Message,
                    "Köprü başlatılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            MonitorButton.IsEnabled = true;
            EndpointPicker.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _cancelGeneration);
        _session = SessionMode.Off;
        _source.Stop();
        _sensa.Stop();
        _leftPhysical = _rightPhysical = false;
        RefreshStatus();
    }

    private async void TestLeft_Click(object sender, RoutedEventArgs e) =>
        await TestSideAsync(HapticTestChannel.Left);

    private async void TestRight_Click(object sender, RoutedEventArgs e) =>
        await TestSideAsync(HapticTestChannel.Right);

    private async Task TestSideAsync(HapticTestChannel side)
    {
        if (_busy) return;
        _busy = true;
        TestLeftButton.IsEnabled = TestRightButton.IsEnabled = false;
        string label = side == HapticTestChannel.Left ? "Sol" : "Sağ";
        try
        {
            // Prevent UAC PCM frame submission from masking isolated side pulse.
            _session = SessionMode.Off;
            _source.Stop();
            if (!_sensa.IsActive && !_sensa.Start())
                throw new InvalidOperationException(_sensa.Status);
            for (int i = 0; i < 25 && !_sensa.IsActive && _sensa.IsRunning; ++i)
                await Task.Delay(80, _lifetime.Token);
            if (!_sensa.IsActive) throw new InvalidOperationException(_sensa.Status);

            bool accepted = await _sensa.TestSideAsync(side, _lifetime.Token);
            bool felt = accepted && System.Windows.MessageBox.Show(this,
                label + " Sensa parametrik efekt komutu gönderildi.\n" +
                "Wolverine V3 Pro'nun " + label.ToLowerInvariant() +
                " tarafında titreşimi bizzat hissettin mi?",
                "Sensa aktüatör testi", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (side == HapticTestChannel.Left) _leftPhysical = felt;
            else _rightPhysical = felt;
            PhysicalTestText.Text = "Sol fiziksel teyit: " + (_leftPhysical ? "VAR" : "YOK") +
                " | Sağ fiziksel teyit: " + (_rightPhysical ? "VAR" : "YOK") +
                ". Yalnızca kullanıcı bildirimi; bağımsız motor ayrımı ayrıca ölçülmeli.";
            DiagnosticsService.Info("Sensa physical " + label + " reported=" + felt);
        }
        catch (Exception ex)
        {
            PhysicalTestText.Text = "Hata: " + ex.Message;
            DiagnosticsService.Error("Sensa side test", ex);
        }
        finally
        {
            _busy = false;
            TestLeftButton.IsEnabled = TestRightButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void ProbeLeft_Click(object sender, RoutedEventArgs e) =>
        await ProbeSourceAsync(HapticTestChannel.Left);

    private async void ProbeRight_Click(object sender, RoutedEventArgs e) =>
        await ProbeSourceAsync(HapticTestChannel.Right);

    private async Task ProbeSourceAsync(HapticTestChannel side)
    {
        if (_busy) return;
        _busy = true;
        ProbeLeftButton.IsEnabled = ProbeRightButton.IsEnabled = false;
        string label = side == HapticTestChannel.Left ? "SOL" : "SAĞ";
        try
        {
            if (_session != SessionMode.SourceOnly ||
                !_source.IsCapturing || _source.EndpointId is not string id)
                throw new InvalidOperationException(
                    "Önce 'Yalnızca 4ch PCM kaynağını izle' düğmesini kullan.");
            if (!NativePcmTestPlayer.VerifyIsolatedChannel(side))
                throw new InvalidOperationException("Sentetik test örneği kanalları karıştırıyor.");
            long ownBefore = side == HapticTestChannel.Left
                ? _source.LeftActiveBlocks : _source.RightActiveBlocks;
            long otherBefore = side == HapticTestChannel.Left
                ? _source.RightActiveBlocks : _source.LeftActiveBlocks;
            await NativePcmTestPlayer.PlayAsync(id, side, _lifetime.Token);
            await Task.Delay(100, _lifetime.Token);
            long ownAfter = side == HapticTestChannel.Left
                ? _source.LeftActiveBlocks : _source.RightActiveBlocks;
            long otherAfter = side == HapticTestChannel.Left
                ? _source.RightActiveBlocks : _source.LeftActiveBlocks;
            bool isolated = HapticSafetyRules.IsolatedChannelObserved(
                ownBefore, ownAfter, otherBefore, otherAfter);
            SourceProbeText.Text = label + " sentetik PCM kaynak testi: " +
                (isolated ? "SEÇİLEN KANAL AYRI GÖZLENDİ" : "BAŞARISIZ/KARŞI KANAL AKTİF") +
                ". Razer provider, motor çıkışı ve gerçek oyun kaynağı doğrulanmadı.";
            DiagnosticsService.Info("UAC source-only side=" + side + " isolated=" + isolated +
                " selectedDelta=" + (ownAfter - ownBefore) +
                " oppositeDelta=" + (otherAfter - otherBefore));
        }
        catch (Exception ex)
        {
            SourceProbeText.Text = "Kaynak-only testi başarısız: " + ex.Message;
            DiagnosticsService.Error("UAC source-only " + side, ex);
        }
        finally
        {
            _busy = false;
            ProbeLeftButton.IsEnabled = ProbeRightButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void LoopLeft_Click(object sender, RoutedEventArgs e) =>
        await RunFullPathTestAsync(HapticTestChannel.Left);

    private async void LoopRight_Click(object sender, RoutedEventArgs e) =>
        await RunFullPathTestAsync(HapticTestChannel.Right);

    private async Task RunFullPathTestAsync(HapticTestChannel side)
    {
        if (_busy) return;
        _busy = true;
        LoopLeftButton.IsEnabled = LoopRightButton.IsEnabled = false;
        string label = side == HapticTestChannel.Left ? "SOL" : "SAĞ";
        try
        {
            if (_session != SessionMode.SensaBridge ||
                !_source.IsCapturing || !_sensa.IsActive || _source.EndpointId is not string id)
                throw new InvalidOperationException(
                    "Önce 4-kanallı DualSense endpoint'ini seç ve deneysel köprüyü başlat.");
            if (!NativePcmTestPlayer.VerifyIsolatedChannel(side))
                throw new InvalidOperationException("Sentetik test dalga biçiminde kanallar karışmış!");
            long before = side == HapticTestChannel.Left ?
                _source.LeftActiveBlocks : _source.RightActiveBlocks;
            long oppositeBefore = side == HapticTestChannel.Left ?
                _source.RightActiveBlocks : _source.LeftActiveBlocks;
            int beforeRender = _sensa.EngineTicks;
            await NativePcmTestPlayer.PlayAsync(id, side, _lifetime.Token);
            await Task.Delay(100, _lifetime.Token);
            long after = side == HapticTestChannel.Left ?
                _source.LeftActiveBlocks : _source.RightActiveBlocks;
            long oppositeAfter = side == HapticTestChannel.Left ?
                _source.RightActiveBlocks : _source.LeftActiveBlocks;
            bool observed = after > before;
            bool isolated = HapticSafetyRules.IsolatedChannelObserved(
                before, after, oppositeBefore, oppositeAfter);
            bool rendered = _sensa.IsActive && _sensa.EngineTicks > beforeRender;
            bool felt = observed && isolated && rendered && System.Windows.MessageBox.Show(this,
                label + " haptic kanalı sentetik UAC PCM gönderdi.\n" +
                "PCM kanalda görüldü: " + observed + "\n" +
                "Karşı kanal sessiz kaldı: " + isolated + "\n" +
                "Razer provider render döngüsü ilerledi: " + rendered + "\n\n" +
                "Wolverine kolunda " + label + " tarafta titreşim hissettin mi?\n" +
                "(Bu bir oyun efekti değildir ve ham PCM aktarımı değildir.)",
                "4-channel UAC → Sensa full path",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            FullPathTestText.Text = label + " test: UAC kaynak=" + (observed ? "ALINDI" : "YOK") +
                " | Karşı kanal izolasyonu=" + (isolated ? "EVET" : "HAYIR") +
                " | Sensa render döngüsü=" + (rendered ? "ÇAĞRILDI" : "YOK") +
                " | Kullanıcı fiziksel his=" + (felt ? "EVET" : "HAYIR") +
                " | Dalga sadakati=DOĞRULANMADI";
            DiagnosticsService.Info("Full UAC->Sensa " + label + " observed=" + observed +
                " isolated=" + isolated + " provider-loop=" + rendered +
                " physical-reported=" + felt);
        }
        catch (Exception ex)
        {
            FullPathTestText.Text = "Tam yol test hatası: " + ex.Message;
            DiagnosticsService.Error("Full path " + label, ex);
        }
        finally
        {
            _busy = false;
            LoopLeftButton.IsEnabled = LoopRightButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        if (!_busy && _session != SessionMode.Off && !_source.IsCapturing)
        {
            string failure = _source.Status;
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            DiagnosticsService.Info("Native source lost, session stopped: " + failure);
            SourceProbeText.Text = "UAC kaynağı durdu veya koptu: " + failure;
        }
        else if (!_busy && _session == SessionMode.SensaBridge && !_sensa.IsActive)
        {
            string failure = _sensa.Status;
            _session = SessionMode.Off;
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            DiagnosticsService.Info("Razer provider lost, capture stopped: " + failure);
            SourceProbeText.Text = "Razer provider kullanılamıyor; kaynak durduruldu: " + failure;
        }

        NativeCaptureState state = _source.CurrentState;
        bool recent = state is NativeCaptureState.AudioSilentOrSpeakerOnly or
            NativeCaptureState.RearChannelEnergyObserved;
        CaptureHealthText.Text = "Kaynak sağlığı: " + NativeCaptureHealth.Describe(state);
        OutputStateText.Text = _session == SessionMode.SourceOnly
            ? "SOURCE ONLY: Razer DLL'i kullanılmıyor, motor komutu gönderilmiyor."
            : _sensa.Status;
        SourceStateText.Text = "UAC: " + _source.Status +
            " | PCM frame: " + _source.CapturedFrames +
            " | L/R aktif blok: " + _source.LeftActiveBlocks + "/" + _source.RightActiveBlocks +
            " | Speaker RMS (taze): " + (recent ? _source.SpeakerRms : 0).ToString("F3") +
            " | Rear L/R RMS (taze): " + (recent ? _source.LeftRms : 0).ToString("F3") +
            "/" + (recent ? _source.RightRms : 0).ToString("F3") +
            " | Rear peaks (taze): " + (recent ? _source.LeftPeak : 0).ToString("F3") +
            "/" + (recent ? _source.RightPeak : 0).ToString("F3") +
            " | Geçersiz PCM: " + _source.InvalidSamples;
        RenderStateText.Text = "Session: " + _session +
            " | Razer provider aktif: " + _sensa.IsActive +
            " | Render ticks: " + _sensa.EngineTicks +
            " | Kullanıcı fiziksel sol/sağ teyidi: " + _leftPhysical + "/" + _rightPhysical;
        TruthStateText.Text = _session switch
        {
            SessionMode.SourceOnly => "YALNIZCA KAYNAK: Stereo PCM gözlemi mümkündür. " +
                "Motor çıkışı YOK, oyun kaynaklı olduğu doğrulanmadı.",
            SessionMode.SensaBridge => "DENEYSEL: 50Hz parametrik Razer çıkışı. " +
                "Fiziksel stereo ayrımı ve orijinal DualSense sadakati doğrulanmadı.",
            _ => "Native DualSense → Wolverine HD doğrulanmadı; şu anda aktif köprü oturumu yok."
        };
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        Interlocked.Increment(ref _cancelGeneration);
        _session = SessionMode.Off;
        _lifetime.Cancel();
        _source.Dispose();
        _sensa.Dispose();
        _lifetime.Dispose();
    }
}
