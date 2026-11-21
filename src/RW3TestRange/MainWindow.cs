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
        (Out.Ds4, "PS4 · DualShock 4 rumble + ışık", "PS4 kolu destekleyen oyunlar gibi: iki motor ve ışık çubuğu (rapor 0x05)."),
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
        // ── quick table: the pad the game "supports" and its feedback rows (like RW3's matrix) ──
        stack.Children.Add(Header("Hızlı ayar: oyun hangi kolu destekliyor?"));
        stack.Children.Add(Muted("Bir sütun seç: oyun o kolu destekleyen gerçek bir oyun gibi davranır (Xbox One/Series → GameInput, Xbox 360 → XInput, PS4 → DualShock 4 raporu, DualSense → adaptif tetik + HD haptik). Hücreye tıkla: satırı aç/kapat; o kolda olmayan satır en yakın destekleyen kola geçer. RW3'te de aynı kolu seç."));
        var rw3Row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        rw3Row.Children.Add(MakeButton("⇄ RW3'teki seçimi kullan", (_, _) => UseRw3Choice()));
        rw3Row.Children.Add(_rw3Info);
        stack.Children.Add(rw3Row);
        stack.Children.Add(new Border { Child = _table, Background = new SolidColorBrush(Color.FromRgb(15, 24, 41)), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(_tableInfo);
        var manual = new StackPanel();
        foreach (var (flag, title, hint) in Outputs)
        {
            var cb = new CheckBox { Content = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            cb.Click += (_, _) => { _cfg.ManualOutputs = Chosen(); _cfg.Save(); RefreshTable(); };
            _outBoxes[flag] = cb;
            manual.Children.Add(cb);
            manual.Children.Add(Muted(hint, 22));
        }
        manual.Children.Add(new TextBlock { Text = "DualSense ses uç noktası (HD haptik / hoparlör):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 10, 0, 0) });
        manual.Children.Add(_endpoint);
        stack.Children.Add(new Expander { Header = "Gelişmiş: çıkışları elle seç (tablo yerine)", Content = manual, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });

        // ── weapons ──
        stack.Children.Add(Header("Silahlar (LB/RB ile aralarında geçilir)"));
        for (int w = 0; w < RangeGame.Weapons.Length; w++)
        {
            int k = w;
            var cb = new CheckBox { Content = RangeGame.Weapons[w].Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), IsChecked = _cfg.Loadout[w] };
            cb.Click += (_, _) => { _cfg.Loadout[k] = cb.IsChecked == true; if (!_cfg.Loadout.Any(x => x)) { _cfg.Loadout[k] = true; cb.IsChecked = true; } _cfg.Save(); };
            stack.Children.Add(cb);
            stack.Children.Add(Muted(RangeGame.Weapons[w].Feel, 22));
        }

        // ── game settings, as real games offer them ──
        stack.Children.Add(Header("Oyun ayarları"));
        var trig = new ComboBox { ItemsSource = new[] { "Kapalı", "Zayıf", "Orta", "Güçlü" }, SelectedIndex = (int)_cfg.Triggers, MinWidth = 140 };
        trig.SelectionChanged += (_, _) => { _cfg.Triggers = (TriggerIntensity)Math.Max(0, trig.SelectedIndex); _cfg.Save(); };
        stack.Children.Add(Row("Tetik efekti yoğunluğu", trig));
        stack.Children.Add(Slider("HD haptik seviyesi", 0, 200, _cfg.HapticPercent, v => _cfg.HapticPercent = v));
        stack.Children.Add(Slider("Titreşim (rumble) seviyesi", 0, 100, _cfg.RumblePercent, v => _cfg.RumblePercent = v));
        stack.Children.Add(Slider("Müzik sesi", 0, 100, _cfg.MusicPercent, v => _cfg.MusicPercent = v));
        stack.Children.Add(Slider("Efekt sesi", 0, 100, _cfg.SfxPercent, v => _cfg.SfxPercent = v));
        var round = new ComboBox { ItemsSource = new[] { "30 sn", "60 sn", "90 sn", "120 sn", "300 sn" }, MinWidth = 140 };
        int[] rounds = { 30, 60, 90, 120, 300 };
        round.SelectedIndex = Math.Max(0, Array.IndexOf(rounds, _cfg.RoundSeconds));
        round.SelectionChanged += (_, _) => { _cfg.RoundSeconds = rounds[Math.Max(0, round.SelectedIndex)]; _cfg.Save(); };
        stack.Children.Add(Row("Tur süresi", round));
        var invert = new CheckBox { Content = "Ters Y ekseni", Foreground = Brushes.White, IsChecked = _cfg.InvertY, Margin = new Thickness(0, 6, 0, 0) };
        invert.Click += (_, _) => { _cfg.InvertY = invert.IsChecked == true; _cfg.Save(); };
        stack.Children.Add(invert);
        _gyro.IsChecked = _cfg.GyroAim;
        _gyro.Click += (_, _) => { _cfg.GyroAim = _gyro.IsChecked == true; _cfg.Save(); };
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

    private static readonly Brush CellAccent = new SolidColorBrush(Color.FromRgb(61, 123, 255));
    private static readonly Brush CellPanel = new SolidColorBrush(Color.FromRgb(34, 48, 77));
    private static readonly Brush CellCurrent = new SolidColorBrush(Color.FromRgb(23, 36, 67));
    private static readonly Brush CellMuted = new SolidColorBrush(Color.FromRgb(147, 162, 191));

    /// <summary>Draws the quick table and syncs the manual output checkboxes with it.</summary>
    private void RefreshTable()
    {
        var g = _table;
        g.Children.Clear(); g.ColumnDefinitions.Clear(); g.RowDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        foreach (var _ in Profiles) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool table = _cfg.ManualOutputs == null;
        for (int c = 0; c < Profiles.Length; c++)
        {
            var p = Profiles[c];
            var head = new Button
            {
                Content = GameConfig.Label(p), Margin = new Thickness(3), Padding = new Thickness(4, 6, 4, 6), FontWeight = FontWeights.SemiBold,
                Background = p == _cfg.Profile && table ? CellAccent : CellPanel, Foreground = Brushes.White, BorderThickness = new Thickness(0)
            };
            head.Click += (_, _) => SelectProfile(p);
            Grid.SetColumn(head, c + 1);
            g.Children.Add(head);
        }
        for (int r = 0; r < Rows.Length; r++)
        {
            var f = Rows[r];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = GameConfig.Label(f), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            Grid.SetRow(label, r + 1); g.Children.Add(label);
            for (int c = 0; c < Profiles.Length; c++)
            {
                var p = Profiles[c];
                bool supported = GameConfig.Supports(p, f), current = p == _cfg.Profile && table, on = current && _cfg.Features.HasFlag(f);
                var cell = new Button
                {
                    Content = supported ? (current ? (on ? "●" : "○") : "✓") : "—",
                    Margin = new Thickness(3), Padding = new Thickness(0, 5, 0, 5), FontSize = 15, BorderThickness = new Thickness(1), BorderBrush = CellPanel,
                    Background = on ? CellAccent : current ? CellCurrent : Brushes.Transparent,
                    Foreground = on ? Brushes.White : CellMuted,
                    ToolTip = !supported ? $"{GameConfig.Label(p)} kolunda yok · tıkla: {GameConfig.Label(BestFor(f, p))} kola geç ve aç"
                        : current ? (on ? "Açık · tıkla: kapat" : "Kapalı · tıkla: aç") : $"tıkla: {GameConfig.Label(p)} kola geç ve aç"
                };
                cell.Click += (_, _) => ClickCell(p, f);
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                g.Children.Add(cell);
            }
        }
        var outs = EffectiveOutputs();
        foreach (var (flag, cb) in _outBoxes) cb.IsChecked = (outs & flag) != 0;
        _tableInfo.Text = !table
            ? "Elle seçilmiş çıkışlar kullanılıyor (tablodan bir hücreye tıklayınca tabloya döner): " + outs
            : _cfg.Profile == PadProfile.Native
                ? NativeNote() + $" · çıkışlar: {outs}"
                : $"Oyun şöyle davranacak: {GameConfig.Label(_cfg.Profile)} destekli oyun · çıkışlar: {outs}";
    }

    private Out Chosen() => _outBoxes.Where(kv => kv.Value.IsChecked == true).Aggregate(Out.None, (a, kv) => a | kv.Key);

    private Out EffectiveOutputs() =>
        _cfg.ManualOutputs ?? (_cfg.Profile == PadProfile.Native
            ? GameConfig.OutputsForReal(_cfg.Features, _fb.DualSenses.Any(d => !d.Virtual), HapticOutput.Endpoints().Count > 0,
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
        foreach (var p in DualShock4Hid.FindAll())
        {
            if (p.Open()) { _fb.Ds4s.Add(p); _input.Sources.Add(p); }
            sb.AppendLine($"DualShock 4 {p.Name}{(p.Error != null ? " · " + p.Error : "")}");
        }
        var eps = HapticOutput.Endpoints();
        _endpoint.ItemsSource = eps.Select(e => e.Name).ToList();
        _endpoint.Tag = eps;
        if (eps.Count > 0) _endpoint.SelectedIndex = Math.Max(0, eps.FindIndex(e => e.Name == _cfg.EndpointName));
        RefreshTable();
        var rw3 = GameConfig.FromRw3();
        _rw3Info.Text = rw3 != null ? "Şu an " + rw3.Value.Text : "RW3 ayarı bulunamadı.";
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
            var eps = _endpoint.Tag as List<(string Id, string Name)>;
            if (_fb.HapticOut == null && eps is { Count: > 0 } && _endpoint.SelectedIndex >= 0)
            {
                _fb.HapticOut = new HapticOutput();
                if (!_fb.HapticOut.Start(eps[_endpoint.SelectedIndex].Id)) _log.Note("HD haptik çıkışı açılamadı: " + _fb.HapticOut.Error);
                else _log.Note("HD haptik çıkışı: " + _fb.HapticOut.Device + " · " + _fb.HapticOut.WaveFormat);
            }
            if (_fb.HapticOut == null) _log.Note("HD haptik seçili ama DualSense ses uç noktası yok.");
        }
        if (_endpoint.SelectedItem is string epName) { _cfg.EndpointName = epName; _cfg.Save(); }
        _log.Event("outputs", new { outputs = _fb.Enabled.ToString(), profile = _cfg.Profile.ToString(), features = _cfg.Features.ToString(), triggers = _cfg.Triggers.ToString(), _cfg.HapticPercent, _cfg.RumblePercent, loadout = string.Join(",", _cfg.Loadout) });
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
        if (s == Screen.Game) { _game = new RangeGame(_fb, _cfg); _surface.Game = _game; }
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
