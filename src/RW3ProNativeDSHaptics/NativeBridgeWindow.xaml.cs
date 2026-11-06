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
    private bool _leftPhysical, _rightPhysical;
    public string StatusForParent =>
        "Kaynak tercihi=" + _route +
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
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false;
            RefreshStatus();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (EndpointPicker.SelectedItem is not EndpointOption selected)
        {
            System.Windows.MessageBox.Show(this, "4 kanallı DualSense ses aygıtını seç.", "DualSense UAC");
            return;
        }
        if (!selected.CanCapture)
        {
            System.Windows.MessageBox.Show(this,
                "Bu aygıt 4 kanallı 48kHz PCM16/Float32 formatını sağlamıyor. " +
                "İşlemi başlatmadan doğru DualSense UAC/Virtual DS aygıtını seç.",
                "UAC formatı uyumsuz", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!HapticRouteCatalog.CanStartBridge(_route, selected.CanCapture,
            EndpointAcknowledgement.IsChecked == true))
        {
            System.Windows.MessageBox.Show(this,
                "Seçtiğin 4ch/48k ses aygıtını ve haricî kaynak eşlemesini doğruladığını " +
                "onaylamadan köprü başlatılamaz.",
                "Kaynak doğrulaması gerekli", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!HapticRouteCatalog.NameMayMatch(_route, selected.FriendlyName) &&
            System.Windows.MessageBox.Show(this,
                "Ses aygıtı adı (" + selected.FriendlyName +
                ") seçilen yöntemin tipik adıyla eşleşmiyor. " +
                "Aygıt ismi kimlik kanıtı değildir; kendin doğruladıysan devam edebilirsin. Devam?",
                "Kaynak adını kontrol et", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
            return;
        if (System.Windows.MessageBox.Show(this,
            "Deneysel sistem 48 kHz dört kanallı DualSense UAC sesini okur ve " +
            "Razer Sensa'ya KAYIPLI sol/sağ titreşim efektleri iletir.\n\n" +
            "Gerçek PCM birebir oynatılmaz. Test motorları düşük şiddette çalışır. Devam?",
            "Experimental Razer bridge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _busy = true;
        StartButton.IsEnabled = false;
        try
        {
            _source.Stop();
            _sensa.Stop();
            _leftPhysical = _rightPhysical = false; // previous run is not proof for this session
            if (!_sensa.Start())
                throw new InvalidOperationException(_sensa.Status);
            // Engine and provider are initialized off the UI thread. Do not block window.
            for (int i = 0; i < 25 && !_sensa.IsActive && _sensa.IsRunning; ++i)
                await Task.Delay(80, _lifetime.Token);
            if (!_sensa.IsActive)
                throw new InvalidOperationException(_sensa.Status);
            _source.Start(selected.Id, _sensa);
            DiagnosticsService.Info("Experimental UAC route=" + _route +
                ", endpoint=" + selected.FriendlyName +
                "; raw PCM → stereo envelopes, not lossless or verified game provenance.");
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Native bridge start", ex);
            _source.Stop();
            _sensa.Stop();
            System.Windows.MessageBox.Show(this, ex.Message, "Native köprü başlatılamadı",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; StartButton.IsEnabled = true; RefreshStatus(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
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
            if (!_source.IsCapturing || !_sensa.IsActive || _source.EndpointId is not string id)
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
        bool source = _source.IsCapturing;
        var last = _source.LastFrameTick;
        bool recent = last != 0 && Environment.TickCount64 - last < 2500;
        OutputStateText.Text = _sensa.Status;
        SourceStateText.Text = "UAC source: " + _source.Status +
            " | Captured frames: " + _source.CapturedFrames +
            " | Last frame: " + (recent ? "recent" : "none/stale") +
            " | Left active blocks: " + _source.LeftActiveBlocks +
            " | Right active blocks: " + _source.RightActiveBlocks +
            " | Speaker RMS: " + _source.SpeakerRms.ToString("F3") +
            " | Haptic L/R RMS: " + _source.LeftRms.ToString("F3") + "/" + _source.RightRms.ToString("F3") +
            " | Haptic L/R peaks: " + _source.LeftPeak.ToString("F3") + "/" + _source.RightPeak.ToString("F3") +
            " | Invalid PCM samples: " + _source.InvalidSamples;
        RenderStateText.Text = "Razer provider initialized: " + _sensa.IsActive +
            " | Provider render ticks: " + _sensa.EngineTicks +
            " | User-confirmed left/right pulse: " + _leftPhysical + " / " + _rightPhysical;
        TruthStateText.Text = source && recent && _sensa.IsActive
            ? "PROTOTYPE: UAC audio frames + Sensa parametric render (source game identity and physical fidelity NOT verified)"
            : "Native DualSense PCM → Wolverine HD is NOT verified. No game signal or provider is currently proven.";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _lifetime.Cancel();
        _source.Dispose();
        _sensa.Dispose();
        _lifetime.Dispose();
    }
}
