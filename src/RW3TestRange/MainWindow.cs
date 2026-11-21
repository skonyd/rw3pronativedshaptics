using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RW3TestRange;

/// <summary>Start screen (outputs, pads, presets) + the game / button test / guided test, one window, any size.</summary>
public sealed class MainWindow : Window
{
    private readonly SessionLog _log = new();
    private readonly FeedbackHub _fb = new();
    private readonly InputHub _input = new();
    private readonly GameSurface _surface = new();
    private RangeGame? _game;
    private Screen _screen = Screen.Setup;
    private readonly Dictionary<Out, CheckBox> _outBoxes = new();
    private readonly CheckBox _gyro = new() { Content = "Jiroskopla nişan (DualSense)", Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _endpoint = new() { MinWidth = 380, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _devices = new() { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12.5 };
    private readonly TextBlock _reportInfo = new() { Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ScrollViewer _setup;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private bool _fullscreen;
    private WindowState _prevState;
    private bool _devicesOpen;

    private static readonly (Out Flag, string Title, string Hint)[] Outputs =
    {
        (Out.Music, "Müzik", "Arka plan müziği hoparlörden/kulaklıktan (RW3 'sesten titreşim' bunu avuçlara çevirir)."),
        (Out.Sfx, "Ses efektleri", "Silah, cam, patlama, kalp atışı sesleri."),
        (Out.XInput, "Xbox · XInput rumble", "Çoğu PC oyununun yolu: iki gövde motoru."),
        (Out.GameInput, "Xbox · GameInput rumble + tetik motorları", "GDK / Game Pass oyunlarının yolu: gövde + LT/RT tetik içi motorlar."),
        (Out.DsHid, "DualSense · adaptif tetikler + ışık + LED", "PS5 oyunları gibi: L2/R2 efektleri, ışık çubuğu, oyuncu LED'leri (HD haptik kapalıysa rumble da)."),
        (Out.DsHaptics, "DualSense · HD haptik", "PS5 oyunları gibi: haptik ses kanalı 3/4 (RW3 sanal DualSense → avuçlar)."),
        (Out.DsSpeaker, "DualSense · kolun hoparlörü", "Efektler kolun kendi hoparlöründen de (kanal 1/2)."),
    };

    public MainWindow(string[] args)
    {
        Title = "Şişe Atışı · RW3 test poligonu";
        Width = 1280; Height = 760; MinWidth = 640; MinHeight = 400;
        Background = new SolidColorBrush(Color.FromRgb(12, 18, 32));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stack = new StackPanel { Margin = new Thickness(28, 20, 28, 20), MaxWidth = 1000 };
        stack.Children.Add(new TextBlock { Text = "Şişe Atışı", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock
        {
            Text = "RW3 test poligonu: oyun kolu gerçek bir oyunun yaptığı gibi sürer, gönderdiği her geri bildirimi kaydeder. Çıkışları seç (birden fazla olabilir), sonra oyunu ya da testleri başlat. F11 / View: tam ekran · Esc: bu ekrana dön.",
            Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14)
        });
        stack.Children.Add(Header("Bulunan kollar ve çıkışlar"));
        stack.Children.Add(_devices);
        var rescan = MakeButton("↻ Yeniden tara", (_, _) => ScanDevices());
        rescan.HorizontalAlignment = HorizontalAlignment.Left; rescan.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(rescan);
        stack.Children.Add(Header("Oyun hangi çıkışları kullansın?"));
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        presets.Children.Add(MakeButton("Xbox oyunu gibi", (_, _) => Preset(Out.Music | Out.Sfx | Out.XInput | Out.GameInput)));
        presets.Children.Add(MakeButton("DualSense oyunu gibi", (_, _) => Preset(Out.Music | Out.Sfx | Out.DsHid | Out.DsHaptics)));
        presets.Children.Add(MakeButton("Yalnız ses (sesten titreşim testi)", (_, _) => Preset(Out.Music | Out.Sfx)));
        presets.Children.Add(MakeButton("Hepsi", (_, _) => Preset(Out.Music | Out.Sfx | Out.XInput | Out.GameInput | Out.DsHid | Out.DsHaptics)));
        stack.Children.Add(presets);
        foreach (var (flag, title, hint) in Outputs)
        {
            var cb = new CheckBox { Content = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), IsChecked = flag is Out.Music or Out.Sfx };
            _outBoxes[flag] = cb;
            stack.Children.Add(cb);
            stack.Children.Add(new TextBlock { Text = hint, Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), Margin = new Thickness(22, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        stack.Children.Add(new TextBlock { Text = "DualSense ses uç noktası (HD haptik / hoparlör):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(_endpoint);
        stack.Children.Add(_gyro);
        stack.Children.Add(Header("Başlat"));
        var go = new WrapPanel();
        go.Children.Add(MakeButton("▶  Oyunu başlat", (_, _) => Enter(Screen.Game), true));
        go.Children.Add(MakeButton("🎮  Tuş testi", (_, _) => Enter(Screen.PadTest)));
        go.Children.Add(MakeButton("📳  Geri bildirim testi (rehberli)", (_, _) => Enter(Screen.Guided)));
        go.Children.Add(MakeButton("📄  Raporu kaydet ve aç", (_, _) => SaveReport(true)));
        stack.Children.Add(go);
        stack.Children.Add(_reportInfo);
        stack.Children.Add(new TextBlock
        {
            Text = "Kolla gezinme: ▲▼ seç, A / ✕ işaretle veya bas. RW3'te 'Oyuna görünen kol' seçimine göre oyun gerçek kolu ya da RW3'ün sanal kolunu görür; RW3 kaydı da rapora eklenir.",
            Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0)
        });
        _setup = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new Grid();
        root.Children.Add(_surface);
        root.Children.Add(_setup);
        _surface.Visibility = Visibility.Collapsed;
        Content = root;
        _surface.Input = _input; _surface.Fb = _fb;

        KeyDown += OnKey;
        Closing += (_, _) => { SaveReport(false); _fb.Dispose(); _log.Dispose(); };
        Loaded += (_, _) => { ScanDevices(); CompositionTarget.Rendering += Frame; _log.Event("app-start", new { args }); };
    }

    private static TextBlock Header(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 6) };

    private static Button MakeButton(string text, RoutedEventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 6), FontSize = 14,
            Background = primary ? new SolidColorBrush(Color.FromRgb(61, 123, 255)) : new SolidColorBrush(Color.FromRgb(34, 48, 77)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        b.Click += click;
        return b;
    }

    private void Preset(Out o)
    {
        foreach (var (flag, cb) in _outBoxes) cb.IsChecked = (o & flag) != 0;
        _log.Event("preset", o.ToString());
    }

    private Out Chosen() => _outBoxes.Where(kv => kv.Value.IsChecked == true).Aggregate(Out.None, (a, kv) => a | kv.Key);

    /// <summary>Finds every pad and output the game can use, and (re)opens them.</summary>
    private void ScanDevices()
    {
        _fb.StopAll();
        foreach (var ds in _fb.DualSenses) ds.Dispose();
        _fb.DualSenses.Clear(); _fb.XPads.Clear(); _input.Sources.Clear();
        _fb.GameInput?.Dispose(); _fb.GameInput = new GameInputHub();
        var sb = new StringBuilder();
        foreach (var x in XInputPad.Present()) { _fb.XPads.Add(x); _input.Sources.Add(x); sb.AppendLine("XInput      " + x.Name); }
        if (_fb.GameInput.Open())
        {
            foreach (var d in _fb.GameInput.Devices) { _input.Sources.Add(d); sb.AppendLine("GameInput   " + d.Name + (d.Vid == 0x045E ? "  (RW3 sanal Xbox kolu olabilir)" : d.Vid == 0x1532 ? "  (Wolverine)" : "")); }
            if (_fb.GameInput.Devices.Count == 0) sb.AppendLine("GameInput   gamepad yok (tetik motorları bu yoldan sürülemez)");
        }
        else sb.AppendLine("GameInput   açılamadı: " + _fb.GameInput.Error);
        if (_fb.XPads.Count == 0) sb.AppendLine("XInput      kol yok");
        foreach (var ds in DualSenseHid.FindAll())
        {
            if (ds.Open()) { _fb.DualSenses.Add(ds); _input.Sources.Add(ds); }
            sb.AppendLine($"DualSense   {ds.Name}{(ds.Error != null ? " · " + ds.Error : "")}\n            {ds.ParentIds}");
        }
        var eps = HapticOutput.Endpoints();
        _endpoint.ItemsSource = eps.Select(e => e.Name).ToList();
        _endpoint.Tag = eps;
        if (eps.Count > 0) _endpoint.SelectedIndex = 0;
        foreach (var e in eps) sb.AppendLine("Ses 4 kanal " + e.Name);
        if (eps.Count == 0) sb.AppendLine("Ses 4 kanal DualSense uç noktası yok (HD haptik / kol hoparlörü kullanılamaz)");
        if (_fb.DualSenses.Count == 0) sb.AppendLine("DualSense   HID yok (adaptif tetik / ışık kullanılamaz)");
        if (_input.Sources.Count == 0) sb.AppendLine("Hiç kol bulunamadı. Kolu bağla ve ↻ Yeniden tara.");
        _devices.Text = sb.ToString().TrimEnd();
        _fb.Speakers ??= new SpeakerOutput();
        if (!_devicesOpen) { _fb.Speakers.Start(); _devicesOpen = true; }
        _log.Event("devices", sb.ToString());
        _log.Note("Kollar: " + string.Join(", ", _input.Sources.Select(s => s.Name)) + " · hoparlör: " + _fb.Speakers.Device);
    }

    private void ApplyOutputs()
    {
        _fb.Enabled = Chosen();
        _fb.Speakers!.SfxOn = _fb.Has(Out.Sfx);
        _fb.Speakers.MusicOn = _fb.Has(Out.Music) && _screen == Screen.Game;
        if (_fb.Has(Out.DsHaptics) || _fb.Has(Out.DsSpeaker))
        {
            var eps = _endpoint.Tag as List<(string Id, string Name)>;
            if (_fb.HapticOut == null && eps is { Count: > 0 } && _endpoint.SelectedIndex >= 0)
            {
                _fb.HapticOut = new HapticOutput();
                if (!_fb.HapticOut.Start(eps[_endpoint.SelectedIndex].Id)) _log.Note("HD haptik çıkışı açılamadı: " + _fb.HapticOut.Error);
                else _log.Note("HD haptik çıkışı: " + _fb.HapticOut.Device + " · " + _fb.HapticOut.WaveFormat);
            }
            if (_fb.HapticOut == null) _log.Note("HD haptik seçili ama DualSense ses uç noktası yok.");
        }
        _log.Event("outputs", _fb.Enabled.ToString());
    }

    private void Enter(Screen s)
    {
        _screen = s;
        ApplyOutputs();
        _setup.Visibility = s == Screen.Setup ? Visibility.Visible : Visibility.Collapsed;
        _surface.Visibility = s == Screen.Setup ? Visibility.Collapsed : Visibility.Visible;
        _surface.Mode = s;
        _log.Event("screen", s.ToString());
        _log.Note("Ekran: " + s + " · çıkışlar: " + _fb.Enabled);
        if (s == Screen.Game) { _game = new RangeGame(_fb) { GyroAim = _gyro.IsChecked == true }; _surface.Game = _game; }
        if (s == Screen.Guided) { _surface.GuidedPhase = "idle"; }
        if (s == Screen.Setup) { _fb.StopAll(); if (_fb.Speakers != null) _fb.Speakers.MusicOn = false; }
        Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.Escape && _screen != Screen.Setup) Enter(Screen.Setup);
        else if (e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen) { _prevState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Normal; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _prevState; }
        _log.Event("fullscreen", _fullscreen);
    }

    private void Frame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(.05, now - _last);
        _last = now;
        _input.Poll();
        foreach (var b in Buttons.All) if (_input.Pressed(b)) { _log.Count("button " + b); if (_screen == Screen.PadTest) _surface.Presses[b] = _surface.Presses.GetValueOrDefault(b) + 1; }
        if (_input.Pressed(Btn.View) && _screen is Screen.Game or Screen.Guided) ToggleFullscreen();
        switch (_screen)
        {
            case Screen.Setup: SetupPad(); break;
            case Screen.Game:
                _game!.Update(dt, _input);
                if ((_game.Paused || _game.Over) && _input.Pressed(Btn.B)) Enter(Screen.Setup);
                break;
            case Screen.PadTest: PadTest(dt); break;
            case Screen.Guided: Guided(dt); break;
        }
        _fb.Update(dt);
        if (_screen != Screen.Setup)
        {
            _surface.Status = $"Giriş: {_input.Active?.Name ?? "-"} · çıkışlar: {_fb.Enabled} · hedef: {_fb.Targets(Out.XInput | Out.GameInput | Out.DsHid)}" +
                (_fb.HapticOut != null ? $" · HD haptik tepe L {_fb.HapticOut.PeakL:0.00} R {_fb.HapticOut.PeakR:0.00}" : "") +
                (_fb.DualSenses.Count > 0 ? $" · DS rapor gir/çık {string.Join("/", _fb.DualSenses.Select(d => $"{d.InputReports}/{d.OutputReports}"))}" : "");
            _surface.InvalidateVisual();
        }
    }

    /// <summary>Start screen with a pad: ▲▼ move focus, A presses / ticks the focused control.</summary>
    private void SetupPad()
    {
        if (!IsActive) return;
        if (_input.Pressed(Btn.Down)) (Keyboard.FocusedElement as UIElement ?? this).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (_input.Pressed(Btn.Up)) (Keyboard.FocusedElement as UIElement ?? this).MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        if (_input.Pressed(Btn.A))
        {
            if (Keyboard.FocusedElement is CheckBox cb) cb.IsChecked = cb.IsChecked != true;
            else if (Keyboard.FocusedElement is Button b) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void PadTest(double dt)
    {
        var s = _input.State;
        // live echo: each trigger drives its own trigger motor as far as it is pulled
        if (_fb.Has(Out.GameInput) && (s.LT > .05f || s.RT > .05f)) _fb.Rumble(0, 0, .06, s.LT * .8f, s.RT * .8f);
        _surface.MenuHold = s.Down(Btn.Menu) ? _surface.MenuHold + dt : 0;
        if (_surface.MenuHold > 2) { _surface.MenuHold = 0; _log.Note("Tuş testi: " + string.Join(", ", _surface.Presses.Select(kv => $"{kv.Key}×{kv.Value}"))); Enter(Screen.Setup); }
    }

    private void Guided(double dt)
    {
        var tests = FeedbackTests.All;
        var t = tests[_surface.GuidedIndex];
        switch (_surface.GuidedPhase)
        {
            case "idle":
                if (_input.Pressed(Btn.Down)) _surface.GuidedIndex = (_surface.GuidedIndex + 1) % tests.Length;
                if (_input.Pressed(Btn.Up)) _surface.GuidedIndex = (_surface.GuidedIndex + tests.Length - 1) % tests.Length;
                if (_input.Pressed(Btn.Menu)) { Enter(Screen.Setup); return; }
                if (_input.Pressed(Btn.A)) Play(t);
                break;
            case "playing":
                _surface.GuidedTime += dt;
                if (_surface.GuidedTime >= t.Seconds) { t.Stop?.Invoke(_fb); _surface.GuidedPhase = "asking"; }
                break;
            case "asking":
                TestAnswer a = _input.Pressed(Btn.A) ? TestAnswer.Yes : _input.Pressed(Btn.B) ? TestAnswer.No : _input.Pressed(Btn.Y) ? TestAnswer.Skipped : TestAnswer.None;
                if (_input.Pressed(Btn.X)) { Play(t); break; }
                if (a != TestAnswer.None) Answer(t, a);
                break;
        }
    }

    private void Play(FeedbackTest t)
    {
        if ((_fb.Enabled & t.Needs) == 0) { Answer(t, TestAnswer.NotAvailable); return; }
        _surface.GuidedPhase = "playing"; _surface.GuidedTime = 0;
        _log.Event("test-play", new { t.Id, t.Title, outputs = (_fb.Enabled & t.Needs).ToString() });
        t.Start(_fb);
    }

    private void Answer(FeedbackTest t, TestAnswer a)
    {
        _surface.Answers[t.Id] = a;
        _log.Event("test-answer", new { t.Id, t.Title, answer = a.ToString() });
        _log.Note($"Test '{t.Title}': {a}");
        _surface.GuidedPhase = "idle";
        _surface.GuidedIndex = Math.Min(FeedbackTests.All.Length - 1, _surface.GuidedIndex + 1);
    }

    private void SaveReport(bool open)
    {
        var sb = new StringBuilder();
        sb.AppendLine("── Çıkışlar ── " + Chosen());
        sb.AppendLine("── Kollar ──\n" + _devices.Text);
        sb.AppendLine("── Geri bildirim testi ──");
        foreach (var t in FeedbackTests.All)
            sb.AppendLine($"  {t.Title,-36} {(_surface.Answers.TryGetValue(t.Id, out var a) ? a.ToString() : "-")}");
        bool sony = _input.Sony;
        var missing = Buttons.All.Where(b => Buttons.Exists(b, sony) && !_surface.Presses.ContainsKey(b)).Select(b => Buttons.Label(b, sony)).ToList();
        sb.AppendLine("── Tuş testi ── " + (_surface.Presses.Count == 0 ? "yapılmadı" : missing.Count == 0 ? "bütün tuşlar çalıştı" : "basılmayan: " + string.Join(" ", missing)));
        if (_fb.HapticOut != null) sb.AppendLine($"── HD haptik ── {_fb.HapticOut.Device}, yazılan kare {_fb.HapticOut.FramesWritten}");
        foreach (var ds in _fb.DualSenses) sb.AppendLine($"── {ds.Name} ── giriş raporu {ds.InputReports}, çıkış raporu {ds.OutputReports}{(ds.Error != null ? ", hata " + ds.Error : "")}");
        try
        {
            string path = _log.Finish(sb.ToString());
            _reportInfo.Text = "Rapor: " + path;
            if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { _reportInfo.Text = "Rapor yazılamadı: " + ex.Message; }
    }
}
