using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace RW3ProNativeDSHaptics;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly UserSettings _settings;
    private readonly XInputHaptics _rumble = new();
    private readonly UpdateService _updates = new();

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _normalMenu;
    private Forms.ToolStripMenuItem? _audioMenu;
    private Forms.ToolStripMenuItem? _nativeMenu;

    private UpdateInfo? _availableUpdate;
    private bool _realExit;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        InitializeTray();
        VersionText.Text = "v" + UpdateService.InstalledVersion;
        AutostartCheckbox.IsChecked = StartupService.IsEnabled;
        _initialized = true;
        SetMode(_settings.Mode, fromUser: false);
    }

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("RW3 Pro Haptics • Aç", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _normalMenu = new Forms.ToolStripMenuItem("◉ Normal Rumble", null,
            (_, _) => Dispatcher.Invoke(() => SetMode(HapticMode.Normal)));
        _audioMenu = new Forms.ToolStripMenuItem("≋ Audio-to-Haptics", null,
            (_, _) => Dispatcher.Invoke(() => SetMode(HapticMode.Audio)));
        _nativeMenu = new Forms.ToolStripMenuItem("⌁ Native DualSense • araştırma", null,
            (_, _) => Dispatcher.Invoke(() => SetMode(HapticMode.Native)));
        menu.Items.Add(_normalMenu);
        menu.Items.Add(_audioMenu);
        menu.Items.Add(_nativeMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Güncellemeleri kontrol et", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(async () => await CheckForUpdatesAsync())));
        menu.Items.Add("Çıkış • motorları durdur", null,
            (_, _) => Dispatcher.Invoke(ExitCompletely));
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "RW3 Pro Native DS Haptics",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreWindow));
    }

    private void SetMode(HapticMode requested, bool fromUser = true)
    {
        _rumble.StopAudio();
        var selected = requested;
        if (requested == HapticMode.Audio)
        {
            if (fromUser && System.Windows.MessageBox.Show(
                "Audio modu Windows ses çıkışını yakalar ve NORMAL XInput rumble oluşturur.\n" +
                "Bu DualSense'in özgün HD efektleri değildir. Devam edilsin mi?",
                "Ses yakalama izni", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                selected = HapticMode.Normal;
            }
            else
            {
                try { _rumble.StartAudio(); }
                catch (Exception e)
                {
                    selected = HapticMode.Normal;
                    if (fromUser) System.Windows.MessageBox.Show(e.Message, "Audio modu başlatılamadı",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        _settings.Mode = selected;
        try { _store.Save(_settings); } catch { }

        NormalIndicator.Visibility = selected == HapticMode.Normal ? Visibility.Visible : Visibility.Collapsed;
        AudioIndicator.Visibility = selected == HapticMode.Audio ? Visibility.Visible : Visibility.Collapsed;
        NativeIndicator.Visibility = selected == HapticMode.Native ? Visibility.Visible : Visibility.Collapsed;
        if (_normalMenu != null) _normalMenu.Checked = selected == HapticMode.Normal;
        if (_audioMenu != null) _audioMenu.Checked = selected == HapticMode.Audio;
        if (_nativeMenu != null) _nativeMenu.Checked = selected == HapticMode.Native;

        TestButton.IsEnabled = selected == HapticMode.Normal;
        if (selected == HapticMode.Normal)
        {
            StatusHeading.Text = "Normal Rumble · hazır";
            StatusDescription.Text = "Oyunun kendi Xbox/XInput titreşimi korunur. İsteğe bağlı kısa motor testi yapılabilir. " +
                "Bu mod Sensa HD veya stereo DualSense PCM aktarımı sağlamaz.";
        }
        else if (selected == HapticMode.Audio)
        {
            StatusHeading.Text = "Audio-to-Haptics · çalışıyor";
            StatusDescription.Text = "Windows'un varsayılan ses çıkışı WASAPI ile dinleniyor ve basit bir DSP üzerinden " +
                "standart XInput rumble üretiliyor. Razer Sensa'nın resmî Audio-to-Haptics motoru değildir. " +
                "Oyun da kendi titreşimini gönderiyorsa iki kaynak birbirini etkileyebilir.";
        }
        else
        {
            StatusHeading.Text = "Native DualSense · henüz desteklenmiyor";
            StatusDescription.Text = "Orijinal DualSense sol/sağ PCM verilerini Wolverine'in iki Sensa voice-coil aktüatörüne " +
                "aktaran doğrulanmış bir gerçek zamanlı donanım çıkışı henüz yok. Burada sahte rumble oynatılmıyor. " +
                "Araştırma ve donanım doğrulama durumu GitHub dokümanlarında.";
        }
        RefreshDevice();
    }

    private void RefreshDevice()
    {
        int index = XInputHaptics.DetectIndex();
        DeviceStatus.Text = index >= 0 ?
            "XInput cihazı algılandı · Slot " + index + " (Wolverine olduğu doğrulanmadı)" :
            "XInput cihazı bulunamadı · Windows PC modunu kontrol et";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshDevice();
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
        RefreshDevice();
    }

    private void ExitCompletely()
    {
        _realExit = true;
        _rumble.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void Normal_Click(object sender, RoutedEventArgs e) => SetMode(HapticMode.Normal);
    private void Audio_Click(object sender, RoutedEventArgs e) => SetMode(HapticMode.Audio);
    private void Native_Click(object sender, RoutedEventArgs e) => SetMode(HapticMode.Native);

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            bool success = await _rumble.TestAsync();
            if (!success)
                System.Windows.MessageBox.Show("Bir XInput kontrolcüsü bağlayın. Birden fazla kol bağlıysa ilk slot kullanılacaktır.",
                    "Motor testi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Motor testi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { TestButton.IsEnabled = _settings.Mode == HapticMode.Normal; }
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText.Text = "GitHub Releases kontrol ediliyor…";
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _availableUpdate = await _updates.CheckAsync();
            if (_availableUpdate == null)
                UpdateStatusText.Text = "Daha yeni bir sürüm bulunamadı veya henüz yayımlanmış Release yok.";
            else
            {
                UpdateStatusText.Text = "Yeni sürüm: v" + _availableUpdate.Version +
                    " · SHA256 kontrolüyle indirmeye hazır.";
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { UpdateStatusText.Text = "Güncelleme kontrol edilemedi: " + ex.Message; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        DownloadUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Paket indiriliyor ve SHA256 doğrulanıyor…";
        try
        {
            var file = await _updates.DownloadVerifiedAsync(_availableUpdate);
            UpdateStatusText.Text = "SHA256 doğrulandı: " + file;
            if (_availableUpdate.IsPortable)
            {
                System.Windows.MessageBox.Show(
                    "Taşınabilir sürüm Downloads klasörüne indirildi. Çalışan uygulamadan tamamen çıkıp ZIP'i ayrı bir klasöre çıkarın; " +
                    "ayarlarınızı koruyun. Uygulama kendini otomatik değiştirmez.",
                    "Portable güncelleme", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenPath(Path.GetDirectoryName(file)!);
            }
            else if (System.Windows.MessageBox.Show(
                "Kurulum dosyası SHA256 ile doğrulandı. Uygulamadan çıkıp kurucuyu açmak ister misiniz?",
                "Güncelleme kurulumu", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                OpenPath(file);
                ExitCompletely();
            }
        }
        catch (Exception ex) { UpdateStatusText.Text = "Güncelleme başarısız: " + ex.Message; }
        finally { DownloadUpdateButton.IsEnabled = true; }
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        try { StartupService.SetEnabled(AutostartCheckbox.IsChecked == true); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Otomatik başlatma", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartCheckbox.IsChecked = StartupService.IsEnabled;
        }
    }

    private static void OpenPath(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });

    private void Github_Click(object sender, RoutedEventArgs e) =>
        OpenPath("https://github.com/skonyd/rw3pronativedshaptics");

    private void Synapse_Click(object sender, RoutedEventArgs e) =>
        OpenPath("https://www.razer.com/synapse-4");
}
