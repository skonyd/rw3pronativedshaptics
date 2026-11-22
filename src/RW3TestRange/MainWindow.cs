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
    private readonly GameConfig _cfg = GameConfig.Load();
    private readonly Grid _table = new();
    private readonly TextBlock _tableInfo = new() { Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) };
    private readonly TextBlock _rw3Info = new() { Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) };
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
    private readonly Grid _setup;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private bool _fullscreen;
    private WindowState _prevState;
    private bool _devicesOpen;
    private WolverineMode _wolverineSeen;
    private double _modeCheck;
    private double _statsTimer;

    private static readonly (Out Flag, string Title, string Hint)[] Outputs =
    {
        (Out.Music, "Müzik", "Arka plan müziği hoparlörden/kulaklıktan (RW3 'sesten titreşim' bunu avuçlara çevirir)."),
        (Out.Sfx, "Ses efektleri", "Silah, cam, patlama, kalp atışı sesleri."),
        (Out.XInput, "Xbox · XInput rumble", "Çoğu PC oyununun yolu: iki gövde motoru."),
        (Out.GameInput, "Xbox · GameInput rumble + tetik motorları", "GDK / Game Pass oyunlarının yolu: gövde + LT/RT tetik içi motorlar."),
        (Out.DsHid, "DualSense · adaptif tetikler + ışık + LED", "PS5 oyunları gibi: L2/R2 efektleri, ışık çubuğu, oyuncu LED'leri (HD haptik kapalıysa rumble da)."),
        (Out.DsHaptics, "DualSense · HD haptik", "PS5 oyunları gibi: haptik ses kanalı 3/4 (RW3 sanal DualSense → avuçlar)."),
        (Out.DsSpeaker, "DualSense · kolun hoparlörü", "Efektler kolun kendi hoparlöründen de (kanal 1/2)."),
        (Out.Ds4, "PS4 · DualShock 4 rumble + ışık", "PS4 kolu destekleyen oyunlar gibi: iki motor ve ışık çubuğu (rapor 0x05)."),
    };

    // ── RW3-style chrome: header with tabs and a status badge, cards, footer ──
    private readonly Dictionary<Screen, RadioButton> _tabs = new();
    private readonly Dictionary<PadProfile, RadioButton> _tiles = new();
    private readonly StackPanel _conn = new();
    private readonly TextBlock _badgeText = new() { Text = "Kol bekleniyor", FontWeight = FontWeights.SemiBold };
    private readonly System.Windows.Shapes.Ellipse _badgeDot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _profileHint = new();
    private bool _syncingTabs;

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];
    private static Style S(string key) => (Style)Application.Current.Resources[key];

    public MainWindow(string[] args)
    {
        Title = "Şişe Atışı · RW3 test oyunu";
        Width = 1180; Height = 860; MinWidth = 760; MinHeight = 560;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _profileHint.Style = S("Hint"); _profileHint.Margin = new Thickness(2, 8, 0, 8);
        _tableInfo.Style = S("Hint"); _rw3Info.Style = S("Hint"); _reportInfo.Style = S("Hint");
        _badgeDot.Fill = B("Muted");

        // header: title · tabs · badge (as on RW3's main window)
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Şişe Atışı", FontSize = 22, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock { Text = "RW3 test oyunu • Xbox ve DualSense destekli", Style = S("Hint") });
        header.Children.Add(title);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (screen, text) in new[] { (Screen.Setup, "Hazırlık"), (Screen.Game, "Oyun"), (Screen.PadTest, "Tuş testi"), (Screen.Guided, "Geri bildirim testi"), (Screen.QuickTest, "Hızlı test") })
        {
            var tab = new RadioButton { Content = text, GroupName = "Tabs", Style = S("TabButton"), IsChecked = screen == Screen.Setup };
            tab.Checked += (_, _) => { if (!_syncingTabs) Enter(screen); };
            _tabs[screen] = tab; tabs.Children.Add(tab);
        }
        var tabBar = new Border { Child = tabs, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x2E)), CornerRadius = new CornerRadius(12), Padding = new Thickness(5) };
        Grid.SetColumn(tabBar, 1); header.Children.Add(tabBar);
        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal };
        badgeRow.Children.Add(_badgeDot); badgeRow.Children.Add(_badgeText);
        var badge = new Border { Child = badgeRow, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x40)), CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 2); header.Children.Add(badge);

        // left: connections (as RW3's "Bağlantılar")
        var left = new DockPanel();
        left.Children.Add(DockTop(new TextBlock { Text = "Bağlantılar", Style = S("CardTitle") }));
        left.Children.Add(DockTop(new TextBlock { Text = "Oyunun gördüğü kollar ve çıkışlar.", Style = S("Hint"), Margin = new Thickness(0, 0, 0, 14) }));
        var rescan = new Button { Content = "↻  Yeniden tara", Style = S("SmallBtn"), HorizontalAlignment = HorizontalAlignment.Left };
        rescan.Click += (_, _) => ScanDevices();
        DockPanel.SetDock(rescan, Dock.Bottom); left.Children.Add(rescan);
        var details = new Expander { Header = "Ayrıntı", Content = _devices, Margin = new Thickness(0, 6, 0, 10), Foreground = B("Muted") };
        _devices.FontSize = 11; _devices.Foreground = B("Muted");
        DockPanel.SetDock(details, Dock.Bottom); left.Children.Add(details);
        left.Children.Add(_conn);
        var leftCard = new Border { Style = S("CardBorder"), Child = left };

        // right: cards
        var right = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        // 1 · which pad the game supports (RW3's selector + matrix, from the game's side)
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6 };
        foreach (var p in Profiles)
        {
            var (label, sub) = TileText(p);
            var tileBody = new StackPanel();
            tileBody.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
            tileBody.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
            tileBody.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Opacity = .75, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var tile = new RadioButton { GroupName = "Profile", Style = S("OutputTile"), Content = tileBody, Tag = p };
            System.Windows.Automation.AutomationProperties.SetName(tile, label);
            tile.Checked += (_, _) => { if (!_syncingTabs) SelectProfile(p); };
            _tiles[p] = tile; tiles.Children.Add(tile);
        }
        var rw3Row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var useRw3 = new Button { Content = "⇄  RW3'teki seçimi kullan", Style = S("SmallBtn") };
        useRw3.Click += (_, _) => UseRw3Choice();
        rw3Row.Children.Add(useRw3); rw3Row.Children.Add(_rw3Info); _rw3Info.VerticalAlignment = VerticalAlignment.Center;
        var matrix = new StackPanel();
        matrix.Children.Add(_table);
        matrix.Children.Add(new TextBlock { Text = "● açık · ○ kapalı · ✓ o kolda var · — yok. Başka bir kolun hücresine tıklayınca o kola geçilir.", Style = S("Hint"), FontSize = 11.5, Margin = new Thickness(4, 6, 0, 0) });
        matrix.Children.Add(_tableInfo); _tableInfo.FontSize = 11.5; _tableInfo.Margin = new Thickness(4, 2, 0, 0);
        var manual = new StackPanel();
        foreach (var (flag, text, hint) in Outputs)
        {
            var cb = new CheckBox { Content = text, Foreground = Foreground, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            cb.Click += (_, _) => { _cfg.ManualOutputs = Chosen(); _cfg.Save(); RefreshTable(); };
            _outBoxes[flag] = cb;
            manual.Children.Add(cb);
            manual.Children.Add(new TextBlock { Text = hint, Style = S("Hint"), Margin = new Thickness(22, 0, 0, 0) });
        }
        manual.Children.Add(new TextBlock { Text = "DualSense ses uç noktası (HD haptik / hoparlör):", Style = S("Hint"), Margin = new Thickness(0, 10, 0, 0) });
        manual.Children.Add(_endpoint);
        right.Children.Add(Card("Oyunun desteklediği kol",
            "Oyun bu kolu destekleyen gerçek bir oyun gibi davranır. RW3'teki 'Oyuna görünen kol' ile aynısını seç; ⇄ düğmesi bunu kendisi yapar.",
            new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x22)), CornerRadius = new CornerRadius(14), Padding = new Thickness(5, 6, 5, 6), Child = tiles, Margin = new Thickness(0, 4, 0, 0) },
            _profileHint,
            new Border { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x18, 0x29)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), BorderBrush = B("Line"), BorderThickness = new Thickness(1), Child = matrix },
            rw3Row,
            new Expander { Header = "Gelişmiş: çıkışları elle seç (tablo yerine)", Content = manual, Foreground = Foreground, Margin = new Thickness(0, 10, 0, 0) }));

        // 2 · weapons
        var weapons = new StackPanel();
        for (int w = 0; w < RangeGame.Weapons.Length; w++)
        {
            int k = w;
            var cb = new CheckBox { Content = RangeGame.Weapons[w].Name, Foreground = Foreground, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0), IsChecked = _cfg.Loadout[w] };
            cb.Click += (_, _) => { _cfg.Loadout[k] = cb.IsChecked == true; if (!_cfg.Loadout.Any(x => x)) { _cfg.Loadout[k] = true; cb.IsChecked = true; } _cfg.Save(); };
            weapons.Children.Add(cb);
            weapons.Children.Add(new TextBlock { Text = RangeGame.Weapons[w].Feel, Style = S("Hint"), Margin = new Thickness(22, 0, 0, 0) });
        }
        right.Children.Add(Card("Silahlar", "Oyunda LB/RB ile aralarında geçilir. Her birinin DualSense tetik efekti ve avuç haptiği ayrıdır.", weapons));

        // 3 · game settings
        var settings = new StackPanel();
        var trig = new ComboBox { ItemsSource = new[] { "Kapalı", "Zayıf", "Orta", "Güçlü" }, SelectedIndex = (int)_cfg.Triggers, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        trig.SelectionChanged += (_, _) => { _cfg.Triggers = (TriggerIntensity)Math.Max(0, trig.SelectedIndex); _cfg.Save(); };
        settings.Children.Add(Row("Tetik efekti yoğunluğu", trig));
        settings.Children.Add(Slider("HD haptik seviyesi", 0, 200, _cfg.HapticPercent, v => _cfg.HapticPercent = v));
        settings.Children.Add(Slider("Titreşim (rumble) seviyesi", 0, 100, _cfg.RumblePercent, v => _cfg.RumblePercent = v));
        settings.Children.Add(Slider("Müzik sesi", 0, 100, _cfg.MusicPercent, v => _cfg.MusicPercent = v));
        settings.Children.Add(Slider("Efekt sesi", 0, 100, _cfg.SfxPercent, v => _cfg.SfxPercent = v));
        var round = new ComboBox { ItemsSource = new[] { "30 sn", "60 sn", "90 sn", "120 sn", "300 sn" }, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        int[] rounds = { 30, 60, 90, 120, 300 };
        round.SelectedIndex = Math.Max(0, Array.IndexOf(rounds, _cfg.RoundSeconds));
        round.SelectionChanged += (_, _) => { _cfg.RoundSeconds = rounds[Math.Max(0, round.SelectedIndex)]; _cfg.Save(); };
        settings.Children.Add(Row("Tur süresi", round));
        var invert = new CheckBox { Content = "Ters Y ekseni", Foreground = Foreground, IsChecked = _cfg.InvertY, Margin = new Thickness(0, 10, 0, 0) };
        invert.Click += (_, _) => { _cfg.InvertY = invert.IsChecked == true; _cfg.Save(); };
        settings.Children.Add(invert);
        _gyro.Foreground = Foreground; _gyro.IsChecked = _cfg.GyroAim;
        _gyro.Click += (_, _) => { _cfg.GyroAim = _gyro.IsChecked == true; _cfg.Save(); };
        settings.Children.Add(_gyro);
        right.Children.Add(Card("Oyun ayarları", "Gerçek oyunlardaki gibi; RW3'ün farklı seviyelere tepkisini denemek için de.", settings));

        // 4 · start
        var go = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var play = new Button { Content = "▶  Oyunu başlat", Style = S("PrimaryBtn"), Margin = new Thickness(0, 0, 8, 6) };
        play.Click += (_, _) => Enter(Screen.Game);
        var padTest = new Button { Content = "🎮  Tuş testi", Style = S("Btn"), Margin = new Thickness(0, 0, 8, 6) };
        padTest.Click += (_, _) => Enter(Screen.PadTest);
        var guided = new Button { Content = "📳  Geri bildirim testi", Style = S("Btn"), Margin = new Thickness(0, 0, 8, 6) };
        guided.Click += (_, _) => Enter(Screen.Guided);
        var quick = new Button { Content = "⚡  Hızlı titreşim testi", Style = S("Btn"), Margin = new Thickness(0, 0, 8, 6) };
        quick.Click += (_, _) => Enter(Screen.QuickTest);
        var report = new Button { Content = "📄  Raporu kaydet ve aç", Style = S("SmallBtn"), Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        report.Click += (_, _) => SaveReport(true);
        go.Children.Add(play); go.Children.Add(quick); go.Children.Add(padTest); go.Children.Add(guided); go.Children.Add(report);
        right.Children.Add(Card("Başlat", "Oyun kapanınca rapor kendiliğinden yazılır; aynı dakikalardaki RW3 kaydı da eklenir.", go, _reportInfo));

        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var setup = new Grid();
        setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        setup.ColumnDefinitions.Add(new ColumnDefinition());
        setup.Children.Add(leftCard);
        Grid.SetColumn(rightScroll, 2); setup.Children.Add(rightScroll);
        _setup = setup;

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock { Text = "Oyun kolu gerçek bir oyun gibi sürer ve gönderdiği her geri bildirimi kaydeder. Ses dosyası yoktur; sesler ve haptikler kodla üretilir.", Style = S("Hint"), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x2E)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(12, 0, 0, 0),
            Child = new TextBlock { Text = "LB/RB sekme · ▲▼ gezin · A seç · F11 tam ekran · Esc geri", Foreground = B("Muted") } };
        Grid.SetColumn(chip, 1); footer.Children.Add(chip);

        var content = new Grid();
        content.Children.Add(_surface);
        content.Children.Add(_setup);
        _surface.Visibility = Visibility.Collapsed;
        var root = new Grid { Margin = new Thickness(28, 20, 28, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        Grid.SetRow(content, 1); root.Children.Add(content);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        _surface.Input = _input; _surface.Fb = _fb;

        KeyDown += OnKey;
        Closing += (_, _) => { SaveReport(false); _fb.Dispose(); _log.Dispose(); };
        Loaded += (_, _) =>
        {
            _ = Task.Run(Synth.Warm); ScanDevices(); CompositionTarget.Rendering += Frame; _log.Event("app-start", new { args });
            // RW3's Test tab opens the quick test directly (like PowerA's test page)
            // (explorer.exe cannot pass arguments, so RW3 leaves a fresh flag file instead of --quick)
            string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3TestRange", "quick-start.flag");
            bool flagged = false;
            try { if (File.Exists(flag)) { flagged = DateTime.Now - File.GetLastWriteTime(flag) < TimeSpan.FromSeconds(30); File.Delete(flag); } } catch { }
            if (flagged || args.Any(a => a.Equals("--quick", StringComparison.OrdinalIgnoreCase))) Enter(Screen.QuickTest);
        };
    }

    private static UIElement DockTop(UIElement e) { DockPanel.SetDock(e, Dock.Top); return e; }

    /// <summary>An RW3 card: title, hint, then the content.</summary>
    private static Border Card(string title, string? hint, params UIElement[] children)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, Style = S("CardTitle") });
        if (hint != null) p.Children.Add(new TextBlock { Text = hint, Style = S("Hint"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var c in children) p.Children.Add(c);
        return new Border { Style = S("CardBorder"), Child = p, Margin = new Thickness(0, 0, 0, 14) };
    }

    private static (string Label, string Sub) TileText(PadProfile p) => p switch
    {
        PadProfile.Native => ("Bağlı kol", "RW3'süz"),
        PadProfile.Physical => ("Wolverine", GameConfig.Wolverine switch { WolverineMode.Pc => "kendisi · PC modu", WolverineMode.Xbox => "kendisi · Xbox modu", _ => "kolun kendisi" }),
        PadProfile.Xbox360 => ("Xbox 360", "XInput"),
        PadProfile.XboxSeries => ("Xbox Series", "tetikli"),
        PadProfile.DualShock4 => ("PS4", "DualShock 4"),
        _ => ("DualSense", "PS5")
    };

    /// <summary>One connection line, like RW3's: coloured dot, name, short state.</summary>
    private void Conn(bool? ok, string name, string hint)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Fill = ok == true ? B("Good") : ok == false ? B("Bad") : B("Muted") });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        t.Children.Add(new TextBlock { Text = hint, Style = S("Hint") });
        Grid.SetColumn(t, 1); g.Children.Add(t);
        _conn.Children.Add(g);
    }

    private static TextBlock Muted(string text, double indent = 0) => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromRgb(147, 162, 191)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 0, 0, 0)
    };

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var p = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var t = new TextBlock { Text = label, Foreground = Brushes.White, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        p.Children.Add(t); p.Children.Add(control);
        return p;
    }

    private FrameworkElement Slider(string label, int min, int max, int value, Action<int> set)
    {
        var val = new TextBlock { Foreground = Brushes.White, Width = 50, VerticalAlignment = VerticalAlignment.Center, Text = $"%{value}" };
        var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 5, IsSnapToTickEnabled = true, MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => { set((int)e.NewValue); val.Text = $"%{(int)e.NewValue}"; _cfg.Save(); };
        var p = new DockPanel();
        DockPanel.SetDock(val, Dock.Right);
        p.Children.Add(val); p.Children.Add(sl);
        return Row(label, p);
    }

    private static readonly PadProfile[] Profiles = { PadProfile.Native, PadProfile.Physical, PadProfile.Xbox360, PadProfile.XboxSeries, PadProfile.DualShock4, PadProfile.DualSense };
    private static readonly GameFeature[] Rows = { GameFeature.Music, GameFeature.Sfx, GameFeature.HdHaptics, GameFeature.Rumble, GameFeature.Triggers };

    /// <summary>Closest profile with a feature: HD haptics → DualSense; triggers → Series from the Xbox side, DualSense from PS4.</summary>
    private static PadProfile BestFor(GameFeature f, PadProfile from) =>
        GameConfig.Supports(from, f) ? from : f == GameFeature.HdHaptics ? PadProfile.DualSense
        : f == GameFeature.Triggers ? (from == PadProfile.DualShock4 ? PadProfile.DualSense : PadProfile.XboxSeries) : from;

    private void ClickCell(PadProfile column, GameFeature f)
    {
        var target = column;
        if (!GameConfig.Supports(target, f)) target = BestFor(f, target);
        if (!GameConfig.Supports(target, f)) return;
        bool switching = target != _cfg.Profile || _cfg.ManualOutputs != null;
        var set = _cfg.Features;
        if (switching) set |= f; else set ^= f;
        foreach (var row in Rows) if (!GameConfig.Supports(target, row)) set &= ~row;
        _cfg.Profile = target; _cfg.Features = set; _cfg.ManualOutputs = null; _cfg.Save();
        _log.Event("quick-table", new { profile = target.ToString(), features = set.ToString() });
        RefreshTable();
    }

    private void SelectProfile(PadProfile p)
    {
        var set = _cfg.Features;
        if (p == PadProfile.DualSense && _cfg.Profile != PadProfile.DualSense) set |= GameFeature.HdHaptics | GameFeature.Triggers;
        if (p == PadProfile.Native && _cfg.Profile != PadProfile.Native) set = GameFeature.Music | GameFeature.Sfx | GameFeature.HdHaptics | GameFeature.Rumble | GameFeature.Triggers;
        if (p is PadProfile.XboxSeries or PadProfile.Physical && _cfg.Profile is not (PadProfile.XboxSeries or PadProfile.Physical)) set |= GameFeature.Triggers;
        foreach (var row in Rows) if (!GameConfig.Supports(p, row)) set &= ~row;
        _cfg.Profile = p; _cfg.Features = set; _cfg.ManualOutputs = null; _cfg.Save();
        _log.Event("quick-table", new { profile = p.ToString(), features = set.ToString() });
        RefreshTable();
    }

    private void UseRw3Choice()
    {
        var r = GameConfig.FromRw3();
        if (r == null) { _rw3Info.Text = "RW3 ayarı okunamadı (RW3 kurulu ve bir kez açılmış olmalı)."; return; }
        _cfg.Profile = r.Value.Profile; _cfg.Features = r.Value.Features; _cfg.ManualOutputs = null; _cfg.Save();
        _rw3Info.Text = r.Value.Text;
        _log.Note("RW3 seçimi kullanıldı: " + r.Value.Text);
        RefreshTable();
    }

    /// <summary>Draws the matrix exactly like RW3's (row label + hint, one column per pad, MatrixCell buttons), syncs the
    /// tiles and the manual output checkboxes.</summary>
    private void RefreshTable()
    {
        bool table = _cfg.ManualOutputs == null;
        _syncingTabs = true;
        try
        {
            foreach (var (p, tile) in _tiles)
            {
                tile.IsChecked = table && p == _cfg.Profile;
                if (tile.Content is StackPanel sp && sp.Children.Count > 2 && sp.Children[2] is TextBlock sub) sub.Text = TileText(p).Sub;
            }
        }
        finally { _syncingTabs = false; }
        _profileHint.Text = table ? ProfileText(_cfg.Profile) : "Elle seçilmiş çıkışlar kullanılıyor; bir kolu ya da hücreyi seçince tabloya dönülür.";

        var g = _table;
        g.Children.Clear(); g.ColumnDefinitions.Clear(); g.RowDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star), MinWidth = 150 });
        foreach (var _ in Profiles) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 92 });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < Profiles.Length; c++)
        {
            var p = Profiles[c]; bool cur = table && p == _cfg.Profile;
            var head = new TextBlock
            {
                Text = TileText(p).Label, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                FontWeight = cur ? FontWeights.Bold : FontWeights.Normal, Foreground = cur ? Brushes.White : B("Muted"), Margin = new Thickness(2, 0, 2, 4)
            };
            Grid.SetColumn(head, c + 1); g.Children.Add(head);
        }
        for (int r = 0; r < Rows.Length; r++)
        {
            var f = Rows[r];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 2, 8, 2) };
            label.Children.Add(new TextBlock { Text = GameConfig.Label(f), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            label.Children.Add(new TextBlock { Text = GameConfig.Sub(f), Foreground = B("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            Grid.SetRow(label, r + 1); g.Children.Add(label);
            for (int c = 0; c < Profiles.Length; c++)
            {
                var p = Profiles[c];
                bool supported = GameConfig.Supports(p, f), current = table && p == _cfg.Profile, on = current && _cfg.Features.HasFlag(f);
                var cell = new Button
                {
                    Style = S("MatrixCell"), BorderBrush = B("Line"),
                    Content = supported ? (current ? (on ? "●" : "○") : "✓") : "—",
                    Background = on ? B("Accent") : current ? new SolidColorBrush(Color.FromRgb(0x17, 0x24, 0x43)) : Brushes.Transparent,
                    Foreground = on ? Brushes.White : B("Muted"),
                    FontSize = on ? 17 : 14,
                    ToolTip = !supported ? $"{TileText(p).Label} kolunda yok · tıkla: {TileText(BestFor(f, p)).Label} koluna geç ve aç"
                        : current ? (on ? "Açık · tıkla: kapat" : "Kapalı · tıkla: aç") : $"{TileText(p).Label} kolunda var · tıkla: o kola geç ve aç"
                };
                System.Windows.Automation.AutomationProperties.SetName(cell, $"{GameConfig.Label(f)} · {TileText(p).Label}");
                cell.Click += (_, _) => ClickCell(p, f);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                g.Children.Add(cell);
            }
        }
        var outs = EffectiveOutputs();
        foreach (var (flag, cb) in _outBoxes) cb.IsChecked = (outs & flag) != 0;
        _tableInfo.Text = !table ? "Çıkışlar: " + outs
            : _cfg.Profile == PadProfile.Native ? NativeNote() + $" · çıkışlar: {outs}"
            : _cfg.Profile == PadProfile.Physical ? GameConfig.Wolverine switch
                {
                    WolverineMode.Pc => "Wolverine PC modunda: Windows onu Xbox 360 kolu olarak görür, oyun XInput ile iki gövde motorunu sürer. Tetik motorları PC modunda yok. RW3'ün sesten avuç titreşimi oyun öndeyken de susmaz.",
                    WolverineMode.Xbox => "Wolverine Xbox modunda: oyun GameInput ile gövde + tetik motorlarını sürer ve kolu tutar (Game Pass oyunları gibi). Oyun öndeyken Windows avuçları susturabilir: bu sütun tam olarak bunu test eder.",
                    _ => "Wolverine bağlı değil: kolu bağla ve ↻ Yeniden tara."
                } + $" · çıkışlar: {outs}"
            : $"Çıkışlar: {outs}";
    }

    private static string ProfileText(PadProfile p) => p switch
    {
        PadProfile.Native => "RW3 yokmuş gibi: bağlı gerçek kollar (kendi DualSense'in, Xbox kolun, PS4 kolun) kendi yollarıyla sürülür; RW3'ün sanal kolları yok sayılır. Karşılaştırma için referans.",
        PadProfile.Physical => "Oyun Wolverine'i olduğu gibi kullanır (RW3'te 'Kolun kendisi'). Xbox modunda gövde + tetik motorları, PC modunda iki gövde motoru.",
        PadProfile.Xbox360 => "Xbox 360 kolu destekleyen bir oyun: XInput, iki gövde motoru. Tetik titreşimi yoktur.",
        PadProfile.XboxSeries => "Xbox One / Series kolu destekleyen bir oyun: GameInput ile gövde + tetik motorları.",
        PadProfile.DualShock4 => "PS4 kolu destekleyen bir oyun: rumble ve ışık çubuğu. Tetik titreşimi yoktur.",
        _ => "DualSense destekleyen bir oyun: adaptif tetikler, ışık, LED'ler ve HD haptik (ses kanalı 3/4)."
    };

    private Out Chosen() => _outBoxes.Where(kv => kv.Value.IsChecked == true).Aggregate(Out.None, (a, kv) => a | kv.Key);

    private Out EffectiveOutputs() =>
        _cfg.ManualOutputs ?? (_cfg.Profile == PadProfile.Native
            ? GameConfig.OutputsForReal(_cfg.Features, _fb.DualSenses.Any(d => !d.Virtual),
                HapticOutput.Endpoints().Any(e => _fb.DualSenses.Any(d => !d.Virtual && d.Container != null && d.Container == e.Container)),
                _fb.Ds4s.Any(d => !d.Virtual), _fb.GameInput?.Devices.Any(d => !d.Sony) == true, _fb.XPads.Count > 0)
            : GameConfig.OutputsFor(_cfg.Profile, _cfg.Features));

    private bool NativeMode => _cfg.Profile == PadProfile.Native && _cfg.ManualOutputs == null;

    /// <summary>Native column: which real pads the game will use, and whether RW3 is emulating a pad right now.</summary>
    private string NativeNote()
    {
        var real = new List<string>();
        real.AddRange(_fb.DualSenses.Where(d => !d.Virtual).Select(d => d.Name));
        real.AddRange(_fb.Ds4s.Where(d => !d.Virtual).Select(d => d.Name));
        if (_fb.GameInput?.Devices.Any(d => !d.Sony) == true) real.Add("Xbox kolu (GameInput: rumble + tetik motorları)");
        else if (_fb.XPads.Count > 0) real.Add("Xbox kolu (XInput: rumble)");
        string text = real.Count > 0 ? "Bağlı kollar kendi yollarıyla kullanılır: " + string.Join(", ", real) + "."
            : "Gerçek kol bulunamadı: kolunu bağla ve ↻ Yeniden tara.";
        var rw3 = GameConfig.FromRw3();
        bool rw3Running = System.Diagnostics.Process.GetProcessesByName("RW3ProNativeDSHaptics").Length > 0;
        if (rw3Running && rw3 is { } r && r.Profile != PadProfile.Physical)
            text += $" ⚠ RW3 açık ve {GameConfig.Label(r.Profile)} taklit ediyor: Wolverine oyundan gizli, sanal kol yok sayılır. Kendi kolunla saf test için RW3'te 'Kolun kendisi'ni seç ya da RW3'ü kapat.";
        else if (rw3Running) text += " RW3 açık ama kol taklit etmiyor (Kolun kendisi); Wolverine'i sesten titreşimle birlikte görürsün.";
        return text;
    }

    /// <summary>Finds every pad and output the game can use, and (re)opens them.</summary>
    private void ScanDevices()
    {
        _fb.StopAll();
        foreach (var ds in _fb.DualSenses) ds.Dispose();
        foreach (var p in _fb.Ds4s) p.Dispose();
        _fb.DualSenses.Clear(); _fb.Ds4s.Clear(); _fb.XPads.Clear(); _input.Sources.Clear();
        _fb.GameInput?.Dispose(); _fb.GameInput = new GameInputHub();
        var sb = new StringBuilder();
        GameConfig.Wolverine = GameConfig.DetectWolverine();
        _wolverineSeen = GameConfig.Wolverine;
        sb.AppendLine("Wolverine   " + GameConfig.Wolverine switch
        {
            WolverineMode.Pc => "PC modunda (1532:0A4C): Windows'ta Xbox 360 kolu, tetik motoru yok",
            WolverineMode.Xbox => "Xbox modunda (1532:0A3F): Xbox One/Series kolu, tetik motorları var",
            _ => "bağlı değil"
        });
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
            sb.AppendLine($"DualSense   {ds.Name}{(ds.Error != null ? " · " + ds.Error : "")} · feature {ds.Features}\n            {ds.ParentIds}");
        }
        foreach (var p in DualShock4Hid.FindAll())
        {
            if (p.Open()) { _fb.Ds4s.Add(p); _input.Sources.Add(p); }
            sb.AppendLine($"DualShock 4 {p.Name}{(p.Error != null ? " · " + p.Error : "")}");
        }
        var eps = HapticOutput.Endpoints();
        _endpoint.ItemsSource = eps.Select(e => e.Name).ToList();
        _endpoint.Tag = eps;
        if (eps.Count > 0) _endpoint.SelectedIndex = ChooseEndpoint(eps);
        RefreshTable();
        var rw3 = GameConfig.FromRw3();
        _rw3Info.Text = rw3 != null ? "Şu an " + rw3.Value.Text : "RW3 ayarı bulunamadı.";
        string Owner((string Id, string Name, Guid? Container) e)
        {
            var ds = _fb.DualSenses.FirstOrDefault(d => d.Container != null && d.Container == e.Container);
            return e.Name + (ds == null ? "" : ds.Virtual ? " · RW3 sanal" : " · gerçek kol");
        }
        foreach (var e in eps) sb.AppendLine("Ses 4 kanal " + Owner(e));
        if (eps.Count == 0) sb.AppendLine("Ses 4 kanal DualSense uç noktası yok (HD haptik / kol hoparlörü kullanılamaz)");
        if (_fb.DualSenses.Count == 0) sb.AppendLine("DualSense   HID yok (adaptif tetik / ışık kullanılamaz)");
        if (_input.Sources.Count == 0) sb.AppendLine("Hiç kol bulunamadı. Kolu bağla ve ↻ Yeniden tara.");
        _devices.Text = sb.ToString().TrimEnd();
        _conn.Children.Clear();
        Conn(GameConfig.Wolverine != WolverineMode.None ? true : null, "Wolverine V3 Pro", GameConfig.Wolverine switch
        {
            WolverineMode.Pc => "PC modunda: Xbox 360 kolu gibi görünür, tetik motoru yok.",
            WolverineMode.Xbox => "Xbox modunda: Xbox One/Series kolu, tetik motorları var.",
            _ => "Bağlı değil."
        });
        Conn(_fb.XPads.Count > 0 ? true : null, "Xbox kolu (XInput)", _fb.XPads.Count > 0 ? string.Join(", ", _fb.XPads.Select(x => x.Name)) : "Yok.");
        int giXbox = _fb.GameInput?.Devices.Count(d => !d.Sony) ?? 0;
        Conn(giXbox > 0 ? true : null, "Xbox kolu (GameInput)", giXbox > 0 ? $"{giXbox} kol · rumble + tetik motorları" : "Yok: tetik motorları bu yoldan sürülemez.");
        Conn(_fb.DualSenses.Count > 0 ? true : null, "DualSense", _fb.DualSenses.Count > 0 ? string.Join(", ", _fb.DualSenses.Select(d => d.Name)) : "Yok (USB kablo gerekli).");
        Conn(eps.Count > 0 ? true : null, "DualSense ses (HD haptik)", eps.Count > 0 ? string.Join(", ", eps.Select(Owner)) : "Yok.");
        if (_fb.Ds4s.Count > 0) Conn(true, "PS4 kolu", string.Join(", ", _fb.Ds4s.Select(d => d.Name)));
        Conn(_fb.Speakers?.Error == null ? true : false, "Hoparlör / kulaklık", _fb.Speakers?.Device ?? "Varsayılan ses aygıtı");
        _fb.Speakers ??= new SpeakerOutput();
        if (!_devicesOpen) { _fb.Speakers.Start(); _devicesOpen = true; }
        _log.Event("devices", sb.ToString());
        _log.Note("Kollar: " + string.Join(", ", _input.Sources.Select(s => s.Name)) + " · hoparlör: " + _fb.Speakers.Device);
    }

    private void ApplyOutputs()
    {
        _fb.Enabled = EffectiveOutputs();
        _fb.RealOnly = _input.RealOnly = NativeMode;
        _fb.RumbleOn = _cfg.ManualOutputs != null || _cfg.Features.HasFlag(GameFeature.Rumble);
        _fb.TriggersOn = (_cfg.ManualOutputs != null || _cfg.Features.HasFlag(GameFeature.Triggers)) && _cfg.Triggers != TriggerIntensity.Off;
        _fb.RumbleScale = _cfg.RumblePercent / 100f;
        _fb.HapticGain = _cfg.HapticPercent / 100f;
        _fb.SfxGain = _cfg.SfxPercent / 100f;
        _fb.Speakers!.Music.Volume = _cfg.MusicPercent / 100f * .8f;
        _fb.Speakers.SfxOn = _fb.Has(Out.Sfx);
        _fb.Speakers.MusicOn = _fb.Has(Out.Music) && _screen == Screen.Game;
        if (_fb.Has(Out.DsHaptics) || _fb.Has(Out.DsSpeaker))
        {
            var eps = _endpoint.Tag as List<(string Id, string Name, Guid? Container)>;
            if (eps is { Count: > 0 } && _cfg.ManualOutputs == null) _endpoint.SelectedIndex = ChooseEndpoint(eps);
            string? want = eps is { Count: > 0 } && _endpoint.SelectedIndex >= 0 ? eps[_endpoint.SelectedIndex].Id : null;
            if (_fb.HapticOut != null && want != null && _hapticEndpoint != want) { _fb.HapticOut.Dispose(); _fb.HapticOut = null; }
            if (_fb.HapticOut == null && want != null)
            {
                _fb.HapticOut = new HapticOutput();
                _hapticEndpoint = want;
                if (!_fb.HapticOut.Start(want)) _log.Note("HD haptik çıkışı açılamadı: " + _fb.HapticOut.Error);
                else _log.Note("HD haptik çıkışı: " + _fb.HapticOut.Device + " · " + _fb.HapticOut.WaveFormat);
            }
            if (_fb.HapticOut == null) _log.Note("HD haptik seçili ama DualSense ses uç noktası yok.");
        }
        if (_endpoint.SelectedItem is string epName) { _cfg.EndpointName = epName; _cfg.Save(); }
        _log.Event("outputs", new { outputs = _fb.Enabled.ToString(), profile = _cfg.Profile.ToString(), features = _cfg.Features.ToString(), triggers = _cfg.Triggers.ToString(), _cfg.HapticPercent, _cfg.RumblePercent, loadout = string.Join(",", _cfg.Loadout) });
    }

    private string? _hapticEndpoint;

    /// <summary>The 4-channel endpoint of the pad this game drives, matched by device container: the real DualSense in
    /// "Bağlı kol (RW3'süz)", RW3's virtual one otherwise (when both exist, names alone cannot tell them apart).</summary>
    private int ChooseEndpoint(List<(string Id, string Name, Guid? Container)> eps)
    {
        if (eps.Count == 0) return -1;
        var wanted = NativeMode ? _fb.DualSenses.Where(d => !d.Virtual)
            : _fb.DualSenses.Where(d => d.Virtual).Concat(_fb.DualSenses.Where(d => !d.Virtual));
        foreach (var ds in wanted)
        {
            int i = eps.FindIndex(e => e.Container != null && e.Container == ds.Container);
            if (i >= 0) return i;
        }
        int saved = eps.FindIndex(e => e.Name == _cfg.EndpointName);
        return saved >= 0 ? saved : 0;
    }

    private void Enter(Screen s)
    {
        _screen = s;
        _syncingTabs = true;
        try { if (_tabs.TryGetValue(s, out var tab)) tab.IsChecked = true; } finally { _syncingTabs = false; }
        ApplyOutputs();
        _setup.Visibility = s == Screen.Setup ? Visibility.Visible : Visibility.Collapsed;
        _surface.Visibility = s == Screen.Setup ? Visibility.Collapsed : Visibility.Visible;
        _surface.Mode = s;
        _log.Event("screen", s.ToString());
        _log.Note("Ekran: " + s + " · çıkışlar: " + _fb.Enabled);
        if (s == Screen.Game) { _game = new RangeGame(_fb, _cfg); _surface.Game = _game; }
        if (s == Screen.Guided) { _surface.GuidedPhase = "idle"; }
        if (s == Screen.Setup) { _fb.StopAll(); if (_fb.Speakers != null) _fb.Speakers.MusicOn = false; }
        if (s != Screen.QuickTest && (_quickSending || _quickSound))
        {
            _fb.Direct(0, 0, 0, 0); _quickSending = false;
            if (_quickSound && _fb.Speakers != null) _fb.Speakers.MusicOn = false; _quickSound = false;
        }
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
        _badgeText.Text = _input.Active != null ? "Giriş: " + _input.Active.Name : "Kol bekleniyor";
        _badgeDot.Fill = _input.Active != null ? B("Good") : B("Muted");
        if (_screen == Screen.Setup && IsActive && (_input.Pressed(Btn.RB) || _input.Pressed(Btn.LB)))
        {
            var order = new[] { Screen.Setup, Screen.Game, Screen.PadTest, Screen.Guided, Screen.QuickTest };
            Enter(order[(Array.IndexOf(order, _screen) + (_input.Pressed(Btn.RB) ? 1 : order.Length - 1)) % order.Length]);
            return;
        }
        switch (_screen)
        {
            case Screen.Setup:
                SetupPad();
                // the pad can switch PC ↔ Xbox mode (☰ combo, RW3) while this screen is open
                if ((_modeCheck += dt) > 3)
                {
                    _modeCheck = 0;
                    var m = GameConfig.DetectWolverine();
                    if (m != _wolverineSeen) { _log.Note($"Wolverine modu: {_wolverineSeen} → {m}"); ScanDevices(); }
                }
                break;
            case Screen.Game:
                _game!.Update(dt, _input);
                if ((_game.Paused || _game.Over) && _input.Pressed(Btn.B)) Enter(Screen.Setup);
                break;
            case Screen.PadTest: PadTest(dt); break;
            case Screen.Guided: Guided(dt); break;
            case Screen.QuickTest: QuickTest(dt); break;
        }
        _fb.Update(dt);
        if ((_statsTimer += dt) >= 2)
        {
            _statsTimer = 0;
            var stats = _input.TakeStats();
            if (stats.Count > 0) SessionLog.Current?.Event("input-stats", stats);
            foreach (var ds in _fb.DualSenses) SessionLog.Current?.Event("ds-raw", new { ds.Name, len = ds.LastRawLength, hex = ds.LastRaw });
            if (_screen == Screen.Game && _game != null) SessionLog.Current?.Event("aim", new { x = Math.Round(_game.CrossX, 3), y = Math.Round(_game.CrossY, 3), gyroAim = _game.GyroAim });
        }
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
            else if (Keyboard.FocusedElement is RadioButton rb) rb.IsChecked = true;
            else if (Keyboard.FocusedElement is Button b) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private bool _quickSending, _quickSound;
    private double _quickLog;

    /// <summary>PowerA-style: B + trigger = that trigger motor, A + trigger = that side's body motor, Y + RT = sound on the
    /// default playback device (for RW3's sound → palms). Strength = how far the trigger is pulled.</summary>
    private void QuickTest(double dt)
    {
        var s = _input.State;
        float lt = 0, rt = 0, low = 0, high = 0;
        if (s.Down(Btn.B)) { lt = s.LT; rt = s.RT; }
        if (s.Down(Btn.A)) { low = s.LT; high = s.RT; }
        bool sound = s.Down(Btn.Y) && s.RT > .2f;
        if (_input.Pressed(Btn.X))
        {
            _fb.Direct(0, 0, 0, 0);
            // game-like (no WGI, start) → all pads (WGI) → virtual pad only (WGI) → game-like …
            _quickMode = (_quickMode + 1) % 3;
            _quickVirtualOnly = _quickMode == 2;
            _surface.QVirtualOnly = _quickVirtualOnly; _surface.QMode = _quickMode; _surface.QWgiUsed |= _quickMode != 0;
            _log.Event("quick-test", new { mode = _quickMode });
        }
        if (lt + rt + low + high > .02f || _quickSending)
        {
            string paths = _fb.Direct(low, high, lt, rt, _quickVirtualOnly, noWgi: _quickMode == 0);
            _surface.QPaths = paths;
            _quickSending = lt + rt + low + high > .02f;
            if ((_quickLog += dt) >= .5 && _quickSending) { _quickLog = 0; _log.Event("quick-test", new { low, high, lt, rt, paths, mode = _quickMode }); }
        }
        if (sound != _quickSound && _fb.Speakers != null)
        {
            _quickSound = sound;
            _fb.Speakers.MusicOn = sound;
            if (sound) _fb.Speakers.Music.Volume = .4f + .6f * s.RT;
            _log.Event("quick-test", new { sound });
        }
        if (sound && _fb.Speakers != null) _fb.Speakers.Music.Volume = .4f + .6f * s.RT;
        _surface.QLow = low; _surface.QHigh = high; _surface.QLt = lt; _surface.QRt = rt; _surface.QSound = sound;
        if (_quickMode != 0 && (_quickWgi -= dt) <= 0) { _quickWgi = 1; _surface.QWgi = _fb.Wgi.Count + " · " + _fb.Wgi.Names(); }
        _surface.MenuHold = s.Down(Btn.Menu) ? _surface.MenuHold + dt : 0;
        if (_surface.MenuHold > 2) { _surface.MenuHold = 0; Enter(Screen.Setup); }
    }
    private double _quickWgi;
    private bool _quickVirtualOnly;
    /// <summary>0 = game-like (XInput + GameInput, no WGI; the start mode), 1 = all pads incl. WGI, 2 = virtual pad only.</summary>
    private int _quickMode;

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
        if (NativeMode) sb.AppendLine("── RW3'süz referans ── " + NativeNote());
        sb.AppendLine($"── Oyun profili ── {GameConfig.Label(_cfg.Profile)} · satırlar {_cfg.Features} · çıkışlar {EffectiveOutputs()}{(_cfg.ManualOutputs != null ? " (elle)" : "")}");
        sb.AppendLine($"── Ayarlar ── tetik {_cfg.Triggers} · haptik %{_cfg.HapticPercent} · rumble %{_cfg.RumblePercent} · müzik %{_cfg.MusicPercent} · efekt %{_cfg.SfxPercent} · tur {_cfg.RoundSeconds} sn · silahlar {string.Join(", ", RangeGame.Weapons.Where((w, i) => _cfg.Loadout[i]).Select(w => w.Name))}");
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
