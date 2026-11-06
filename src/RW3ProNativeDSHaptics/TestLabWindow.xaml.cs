using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Game-free diagnostics: source, captured channel, delivery and physical sensation
/// are separate facts. Native fixture by itself is NEVER original game haptics.
/// </summary>
public partial class TestLabWindow : Window
{
    private readonly XInputHaptics _haptics;
    private readonly HapticMode _mode;
    private readonly CancellationTokenSource _closingToken = new();
    private ChannelTestResult? _left;
    private ChannelTestResult? _right;
    private bool _busy;

    public TestLabWindow(HapticMode mode, XInputHaptics haptics)
    {
        InitializeComponent();
        _haptics = haptics;
        _mode = mode;
        ModeHeader.Text = mode switch
        {
            HapticMode.Normal => "MOD: Normal Rumble • XInput",
            HapticMode.Audio => "MOD: Audio-to-Haptics • WASAPI",
            _ => "MOD: Native DualSense • gerçek Sensa PCM çıkışı bekleniyor"
        };
        ModeDescription.Text = mode switch
        {
            HapticMode.Normal =>
                "Sol ve sağ geleneksel XInput motorları ayrı sınanır. Komutun kabul edilmesi " +
                "titreşimi hissettiğini veya Sensa HD'yi kanıtlamaz.",
            HapticMode.Audio =>
                "Seçilen WASAPI ses çıkışına sol veya sağ kanalda kısa bir sinyal gerçekten gönderilir. " +
                "Ses yakalanması, klasik XInput motor komutu ve senin titreşim teyidin ayrı tutulur. " +
                "Bu test gerçek oyun DualSense PCM verisi değildir.",
            _ =>
                "Oyun açmadan 48 kHz stereo sentetik PCM üretip kanalları denetleyebiliriz. " +
                "Bu verinin Wolverine'in Sensa motorlarında oynatıldığı henüz kanıtlanmadı. " +
                "Resmî Razer Sensa TechDemo, ayrı fiziksel motor doğrulaması içindir."
        };
        if (mode == HapticMode.Native)
            OperationText.Text = "Sentetik veri testi mevcut. Gerçek DualSense → Sensa motor aktarımı henüz kanıtlanmadı.";
    }

    private async void Left_Click(object sender, RoutedEventArgs e) =>
        await RunOneAsync(HapticTestChannel.Left);

    private async void Right_Click(object sender, RoutedEventArgs e) =>
        await RunOneAsync(HapticTestChannel.Right);

    private async void Both_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunOneAsync(HapticTestChannel.Left);
        if (!_closingToken.IsCancellationRequested) await RunOneAsync(HapticTestChannel.Right);
    }

    private async Task RunOneAsync(HapticTestChannel side)
    {
        if (_busy || _closingToken.IsCancellationRequested) return;
        _busy = true;
        LeftButton.IsEnabled = RightButton.IsEnabled = BothButton.IsEnabled = false;
        string label = side == HapticTestChannel.Left ? "Sol" : "Sağ";
        OperationText.Text = label + " kanal testi çalışıyor…";
        try
        {
            ChannelTestResult result;
            if (_mode == HapticMode.Normal)
            {
                bool ack = await _haptics.TestChannelAsync(side, _closingToken.Token);
                bool felt = ack && AskUser(label,
                    "XInput " + label + " motor komutu Windows tarafından kabul edildi. " +
                    "Bu kolun " + label.ToLowerInvariant() + " tarafında gerçekten titreşim hissettin mi?\n\n" +
                    "Bu, klasik rumble testidir; Sensa HD doğrulaması değildir.");
                result = new(_mode, side, ack, ack, ack, felt, "Yalnızca geleneksel XInput rumble.");
            }
            else if (_mode == HapticMode.Audio)
            {
                var before = _haptics.Snapshot();
                if (!before.IsCapturing || string.IsNullOrWhiteSpace(before.AudioEndpointId))
                    throw new InvalidOperationException("Önce Audio-to-Haptics modunu başlat ve WASAPI iznini ver.");
                await AudioChannelTest.PlayAsync(before.AudioEndpointId, side, _closingToken.Token);
                await Task.Delay(100, _closingToken.Token);
                var after = _haptics.Snapshot();
                bool observed = side == HapticTestChannel.Left ?
                    after.LeftSoundTick > before.LeftSoundTick : after.RightSoundTick > before.RightSoundTick;
                bool ack = side == HapticTestChannel.Left ?
                    after.LeftOutputAcknowledged > before.LeftOutputAcknowledged :
                    after.RightOutputAcknowledged > before.RightOutputAcknowledged;
                bool felt = observed && ack && AskUser(label,
                    "Sistem ses testi " + label + " WASAPI kanalından geçti ve XInput " +
                    "komutu kabul edildi. Kolun " + label.ToLowerInvariant() +
                    " tarafında titreşim hissettin mi?\n\n" +
                    "Bu HD DualSense değil, sistem sesi → klasik rumble testidir.");
                result = new(_mode, side, true, observed, ack, felt,
                    "Kısa gerçek ses çalındı; başka sistem sesleri ölçümü etkileyebilir.");
            }
            else
            {
                bool valid = NativeStereoFixture.ValidateSeparation(side);
                result = new(_mode, side, valid, false, false, false,
                    valid ? "Stereo kaynak kanalı test edildi; gerçek oyun komutu veya Sensa aktarımı değil." :
                    "Sentetik stereo dalga üretilemedi.");
                OperationText.Text = label + ": sentetik PCM yazılım testi tamamlandı; Wolverine'e gerçek HD komut gönderilmedi.";
            }
            if (side == HapticTestChannel.Left) _left = result; else _right = result;
            UpdateResults();
            DiagnosticsService.Info("Test Lab " + _mode + "/" + side +
                " signal=" + result.TestSignalEmitted + " captured=" + result.SelectedChannelInputObserved +
                " ack=" + result.DeviceCommandAcknowledged + " felt=" + result.PhysicalSensationConfirmed);
        }
        catch (OperationCanceledException)
        {
            OperationText.Text = label + ": iptal edildi.";
        }
        catch (Exception ex)
        {
            OperationText.Text = label + " test hatası: " + ex.Message;
            DiagnosticsService.Error("Test Lab " + side, ex);
            var failed = new ChannelTestResult(_mode, side, false, false, false, false, ex.Message);
            if (side == HapticTestChannel.Left) _left = failed; else _right = failed;
            UpdateResults();
        }
        finally
        {
            _busy = false;
            LeftButton.IsEnabled = RightButton.IsEnabled = BothButton.IsEnabled = true;
        }
    }

    private bool AskUser(string label, string question) =>
        System.Windows.MessageBox.Show(this, question, label + " kanal fiziksel teyidi",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void UpdateResults()
    {
        LeftResult.Text = _left == null ? "Sol: test edilmedi" :
            "Sol: " + _left.Summary;
        RightResult.Text = _right == null ? "Sağ: test edilmedi" :
            "Sağ: " + _right.Summary;
        bool complete = _left?.PhysicalSensationConfirmed == true &&
            _right?.PhysicalSensationConfirmed == true;
        OverallResult.Text = _mode == HapticMode.Native ?
            "Native DualSense aktarımı hâlâ doğrulanmadı. Sentetik PCM üretimi fiziksel haptic başarısı değildir." :
            complete ? "Sol ve sağ kanallar için kullanıcı hissi teyit edildi (klasik titreşim)." :
            "Sol ve sağ kanallarda fiziksel teyit tamamlanmadı.";
    }

    private void ExportLeft_Click(object sender, RoutedEventArgs e) =>
        Export(HapticTestChannel.Left);
    private void ExportRight_Click(object sender, RoutedEventArgs e) =>
        Export(HapticTestChannel.Right);

    private void Export(HapticTestChannel side)
    {
        try
        {
            string file = NativeStereoFixture.ExportWav(side);
            FixtureStatus.Text = "Sentetik stereo referans oluşturuldu: " + file +
                "\nBu WAV gerçek oyundan alınmış DualSense haptic verisi değildir.";
        }
        catch (Exception ex)
        {
            FixtureStatus.Text = "WAV oluşturulamadı: " + ex.Message;
            DiagnosticsService.Error("Export synthetic PCM", ex);
        }
    }

    private void OfficialSensa_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/WyvrnOfficial/RazerSensa_DevKit",
            UseShellExecute = true
        });
        FixtureStatus.Text = "Razer DevKit dokümanı açıldı. TechDemo kurulumu ayrı bir işlemdir; " +
            "RW3 uygulamasının Native PCM çıkışı hâlâ doğrulanmadı.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText("RW3 Haptic Test Lab\nMode: " + _mode +
            "\nLeft: " + (_left?.ToString() ?? "untested") +
            "\nRight: " + (_right?.ToString() ?? "untested") +
            "\nNativeDualSenseHapticsOutputVerified: false\n");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closingToken.Cancel();
        _closingToken.Dispose();
    }
}
