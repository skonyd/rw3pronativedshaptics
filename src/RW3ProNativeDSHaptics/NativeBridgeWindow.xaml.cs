using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RW3ProNativeDSHaptics;

public partial class NativeBridgeWindow : Window
{
    private sealed record EndpointOption(string Id, string Display);
    private readonly SensaStereoPrototype _sensa = new();
    private readonly NativeDualSenseCapture _source = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _leftPhysical, _rightPhysical;

    public NativeBridgeWindow()
    {
        InitializeComponent();
        RefreshSources();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private void RefreshSources()
    {
        string? old = (EndpointPicker.SelectedItem as EndpointOption)?.Id;
        EndpointPicker.Items.Clear();
        try
        {
            foreach (var item in NativeDualSenseCapture.EnumeratePossibleSources())
            {
                // The user must select a real 4-ch DualSense USB/UAC output.
                EndpointPicker.Items.Add(new EndpointOption(item.Id,
                    item.Name + " | " + item.Channels + " channels"));
            }
            foreach (EndpointOption entry in EndpointPicker.Items)
                if (entry.Id == old) { EndpointPicker.SelectedItem = entry; break; }
            if (EndpointPicker.SelectedItem == null && EndpointPicker.Items.Count > 0)
                EndpointPicker.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Native UAC enumerate", ex);
            SourceStateText.Text = "Aygıt listesi okunamadı: " + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (EndpointPicker.SelectedItem is not EndpointOption selected)
        {
            MessageBox.Show(this, "4 kanallı DualSense ses aygıtını seç.", "DualSense UAC");
            return;
        }
        if (MessageBox.Show(this,
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
            if (!_sensa.Start())
                throw new InvalidOperationException(_sensa.Status);
            // Engine and provider are initialized off the UI thread. Do not block window.
            for (int i = 0; i < 25 && !_sensa.IsActive && _sensa.IsRunning; ++i)
                await Task.Delay(80, _lifetime.Token);
            if (!_sensa.IsActive)
                throw new InvalidOperationException(_sensa.Status);
            _source.Start(selected.Id, _sensa);
            DiagnosticsService.Info("Experimental DualSense UAC source started; raw PCM → stereo envelopes, not lossless.");
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("Native bridge start", ex);
            _source.Stop();
            _sensa.Stop();
            MessageBox.Show(this, ex.Message, "Native köprü başlatılamadı",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; StartButton.IsEnabled = true; RefreshStatus(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _source.Stop();
        _sensa.Stop();
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
            bool felt = accepted && MessageBox.Show(this,
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
            " | Right active blocks: " + _source.RightActiveBlocks;
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
