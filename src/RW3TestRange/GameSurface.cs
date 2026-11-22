using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RW3TestRange;

public enum Screen { Setup, Game, PadTest, Guided, QuickTest }

/// <summary>Draws the game, the button test and the guided test with plain shapes (visuals are not the point).</summary>
public sealed class GameSurface : FrameworkElement
{
    public Screen Mode;
    public RangeGame? Game;
    public InputHub? Input;
    public FeedbackHub? Fb;
    public string Status = "";
    // pad test
    public readonly Dictionary<Btn, int> Presses = new();
    public double MenuHold;
    // guided test
    public int GuidedIndex;
    public string GuidedPhase = "idle";
    public double GuidedTime;
    public readonly Dictionary<string, TestAnswer> Answers = new();

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    // RW3's palette (Theme.xaml): card #121C31, card hover #18243D, line #22304D, muted #93A2BF, accent #3D7BFF
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x12, 0x1C, 0x31));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x3D));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFF));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x93, 0xA2, 0xBF));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF));
    private static readonly Brush Glass = new SolidColorBrush(Color.FromRgb(60, 170, 90));
    private static readonly Brush GoldB = new SolidColorBrush(Color.FromRgb(255, 200, 40));
    private static readonly Pen Line = new(new SolidColorBrush(Color.FromRgb(0x22, 0x30, 0x4D)), 2);
    private static readonly Pen Cross = new(Brushes.White, 2);
    private static readonly Pen Shelf = new(new SolidColorBrush(Color.FromRgb(120, 85, 50)), 6);

    private double W => ActualWidth;
    private double H => ActualHeight;
    private double S => Math.Max(.6, Math.Min(W / 1280, H / 720));   // text scale for small and big windows

    private void Text(DrawingContext dc, string s, double x, double y, double size, Brush b, bool bold = false, TextAlignment align = TextAlignment.Left, double maxW = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Face, size * S, b, 1.25) { TextAlignment = align };
        if (maxW > 0) ft.MaxTextWidth = maxW;
        dc.DrawText(ft, new Point(x, y));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, W, H), 14, 14);   // an RW3 card
        switch (Mode)
        {
            case Screen.Game: DrawGame(dc); break;
            case Screen.PadTest: DrawPadTest(dc); break;
            case Screen.Guided: DrawGuided(dc); break;
            case Screen.QuickTest: DrawQuickTest(dc); break;
        }
        Text(dc, Status, 12 * S, H - 26 * S, 12, Muted);
    }

    private void DrawGame(DrawingContext dc)
    {
        var g = Game; if (g == null) return;
        bool sony = Input?.Sony ?? false;
        for (int s = 0; s < 3; s++) { double y = (.3 + s * .22) * H; dc.DrawLine(Shelf, new Point(0, y + 4), new Point(W, y + 4)); }
        foreach (var b in g.Bottles)
        {
            double x = b.X * W, y = b.Y * H, bw = 18 * S, bh = 52 * S;
            var fill = b.Golden ? GoldB : Glass;
            dc.DrawRoundedRectangle(fill, null, new Rect(x - bw / 2, y - bh, bw, bh * .7), 4 * S, 4 * S);
            dc.DrawRectangle(fill, null, new Rect(x - bw / 5, y - bh - 4 * S, bw / 2.5, bh * .35));
        }
        foreach (var p in g.Shards)
            dc.DrawLine(new Pen(p.Gold ? GoldB : Glass, 2), new Point(p.X * W, p.Y * H), new Point(p.X * W + 5, p.Y * H + 3));
        foreach (var p in g.Popups) Text(dc, p.Text, p.X * W, p.Y * H, 16, p.Text.Contains("ALTIN") ? GoldB : Brushes.White, true);
        if (g.ExplosionAt >= 0)
            dc.DrawEllipse(null, new Pen(Warn, 6 * S), new Point(g.ExplosionX * W, g.ExplosionY * H), g.ExplosionRing * W, g.ExplosionRing * H);
        double cx = g.CrossX * W, cy = g.CrossY * H, r = (g.Aiming ? 14 : 26) * S;
        if (g.Flash > 0) dc.DrawEllipse(Warn, null, new Point(cx, cy), r * 1.2, r * 1.2);
        dc.DrawEllipse(null, Cross, new Point(cx, cy), r, r);
        dc.DrawLine(Cross, new Point(cx - r * 1.6, cy), new Point(cx - r * .5, cy));
        dc.DrawLine(Cross, new Point(cx + r * .5, cy), new Point(cx + r * 1.6, cy));
        dc.DrawLine(Cross, new Point(cx, cy - r * 1.6), new Point(cx, cy - r * .5));
        dc.DrawLine(Cross, new Point(cx, cy + r * .5), new Point(cx, cy + r * 1.6));

        var w = g.Current;
        string R2 = sony ? "R2" : "RT", L2 = sony ? "L2" : "LT", Xb = sony ? "□" : "X", Yb = sony ? "△" : "Y";
        Text(dc, $"{w.Name}   {(g.Reloading ? "dolduruluyor…" : $"{g.Ammo[g.WeaponIndex]}/{w.Mag}")}   💣×{g.Grenades}", 14 * S, 10 * S, 20, Brushes.White, true);
        Text(dc, $"Skor {g.Score}   İsabet {g.Hits}/{g.Shots}   Süre {g.TimeLeft:0}", W - 14 * S, 10 * S, 20, g.TimeLeft < 10 ? Bad : Brushes.White, true, TextAlignment.Right);
        Text(dc, $"{R2} ateş · {L2} nişan · LB/RB silah · {Xb} doldur · {Yb} el bombası · RS dipçik · LS haptik süpürme · ▲▼ rumble gücü · ☰ duraklat",
            14 * S, 40 * S, 12.5, Muted);
        double y0 = H - 40 * S - (g.FeedbackLines.Count * 17 * S);
        foreach (var line in g.FeedbackLines) { Text(dc, line, 14 * S, y0, 12.5, Accent); y0 += 17 * S; }
        if (g.Paused) Overlay(dc, "Duraklatıldı", "☰ devam · B menüye dön");
        if (g.Over) Overlay(dc, $"Tur bitti · skor {g.Score}", $"İsabet {g.Hits}/{g.Shots}.  A yeni tur · B menüye dön");
    }

    private void Overlay(DrawingContext dc, string title, string sub)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), null, new Rect(0, 0, W, H));
        Text(dc, title, W / 2, H / 2 - 40 * S, 36, Brushes.White, true, TextAlignment.Center);
        Text(dc, sub, W / 2, H / 2 + 10 * S, 18, Muted, false, TextAlignment.Center);
    }

    // quick test state (set by MainWindow each frame)
    public float QLow, QHigh, QLt, QRt;
    public bool QSound;
    public string QPaths = "-", QWgi = "-";

    /// <summary>PowerA-style quick test: hold a face button and pull a trigger; the pull sets the strength.</summary>
    private void DrawQuickTest(DrawingContext dc)
    {
        var st = Input?.State ?? default;
        Text(dc, "Hızlı titreşim testi · " + (Input?.Active?.Name ?? "kol bekleniyor (bir tuşa bas)"), 20 * S, 14 * S, 22, Brushes.White, true);
        Text(dc, "Bir tuşu basılı tut ve tetiği çek: ne kadar çekersen o kadar güçlü. Komut aynı anda XInput, GameInput ve Windows.Gaming.Input (PowerA'nın yolu) ile gönderilir. Çıkmak için ☰ 2 sn ya da Esc.",
            20 * S, 46 * S, 13, Muted, false, TextAlignment.Left, W - 40 * S);
        double cw = 250 * S, ch = 150 * S, y = 100 * S;
        void CardBox(int i, string title, string l1, string l2, bool on)
        {
            double x = 20 * S + i * (cw + 14 * S);
            dc.DrawRoundedRectangle(on ? new SolidColorBrush(Color.FromRgb(30, 90, 60)) : Panel, Line, new Rect(x, y, cw, ch), 10, 10);
            Text(dc, title, x + 16 * S, y + 14 * S, 20, Brushes.White, true);
            Text(dc, l1, x + 16 * S, y + 56 * S, 15, Brushes.White);
            Text(dc, l2, x + 16 * S, y + 86 * S, 15, Brushes.White);
        }
        CardBox(0, "Tetik (impulse)", "B + RT  → sağ tetik", "B + LT  → sol tetik", QLt + QRt > 0);
        CardBox(1, "Rumble", "A + RT  → sağ (küçük) motor", "A + LT  → sol (büyük) motor", QLow + QHigh > 0);
        CardBox(2, "Ses testi (avuçlar)", "Y + RT  → hoparlörde ses", "RW3 sesi avuçlara çevirir", QSound);
        double by = y + ch + 30 * S;
        Bar(dc, "Sol tetik", QLt, 20 * S, by); Bar(dc, "Sağ tetik", QRt, 100 * S, by);
        Bar(dc, "Sol motor", QLow, 200 * S, by); Bar(dc, "Sağ motor", QHigh, 280 * S, by);
        Bar(dc, "LT", st.LT, 400 * S, by); Bar(dc, "RT", st.RT, 480 * S, by);
        Text(dc, "Son gönderim: " + QPaths, 600 * S, by + 10 * S, 14, Brushes.White);
        Text(dc, "WGI kolları: " + QWgi, 600 * S, by + 36 * S, 13, Muted);
    }

    private void DrawPadTest(DrawingContext dc)
    {
        var st = Input?.State ?? default; bool sony = Input?.Sony ?? false;
        Text(dc, "Tuş testi · " + (Input?.Active?.Name ?? "kol bekleniyor (bir tuşa bas)"), 20 * S, 14 * S, 22, Brushes.White, true);
        Text(dc, "Her tuşa en az bir kez bas; basılanlar yeşil olur. Tetikler sol/sağ tetik motorunu çektiğin kadar titretir (GameInput seçiliyse). Çıkmak için ☰ / Options'ı 2 sn basılı tut ya da Esc.",
            20 * S, 46 * S, 13, Muted, false, TextAlignment.Left, W - 40 * S);
        double x = 20 * S, y = 90 * S, bw = 92 * S, bh = 48 * S;
        int col = 0, needed = 0, done = 0;
        foreach (var b in Buttons.All)
        {
            if (!Buttons.Exists(b, sony)) continue;
            needed++;
            int n = Presses.GetValueOrDefault(b);
            if (n > 0) done++;
            var fill = st.Down(b) ? Accent : n > 0 ? new SolidColorBrush(Color.FromRgb(30, 90, 60)) : Panel;
            dc.DrawRoundedRectangle(fill, Line, new Rect(x + col * (bw + 8 * S), y, bw, bh), 8, 8);
            Text(dc, Buttons.Label(b, sony), x + col * (bw + 8 * S) + bw / 2, y + 6 * S, 15, Brushes.White, true, TextAlignment.Center);
            Text(dc, n > 0 ? $"×{n}" : "—", x + col * (bw + 8 * S) + bw / 2, y + 26 * S, 11, Muted, false, TextAlignment.Center);
            if (++col == 10) { col = 0; y += bh + 8 * S; }
        }
        y += bh + 24 * S;
        Text(dc, done == needed ? $"✓ Bütün tuşlar çalışıyor ({needed}/{needed})" : $"{done}/{needed} tuş denendi", 20 * S, y, 18, done == needed ? Good : Warn, true);
        y += 40 * S;
        Stick(dc, "Sol çubuk", st.LX, st.LY, 90 * S, y + 70 * S);
        Stick(dc, "Sağ çubuk", st.RX, st.RY, 300 * S, y + 70 * S);
        Bar(dc, sony ? "L2" : "LT", st.LT, 440 * S, y + 10 * S);
        Bar(dc, sony ? "R2" : "RT", st.RT, 520 * S, y + 10 * S);
        double tx = 640 * S;
        if (st.HasMotion)
        {
            Text(dc, $"Jiroskop  x {st.GyroX,6:0.0}  y {st.GyroY,6:0.0}  z {st.GyroZ,6:0.0}", tx, y + 10 * S, 14, Brushes.White);
            Text(dc, $"İvme       x {st.AccX,6:0.00}  y {st.AccY,6:0.00}  z {st.AccZ,6:0.00}", tx, y + 34 * S, 14, Brushes.White);
            var pad = new Rect(tx, y + 70 * S, 240 * S, 110 * S);
            dc.DrawRoundedRectangle(Panel, Line, pad, 8, 8);
            if (st.Touching) dc.DrawEllipse(Accent, null, new Point(pad.X + st.TouchX * pad.Width, pad.Y + st.TouchY * pad.Height), 8 * S, 8 * S);
            Text(dc, "Touchpad", pad.X + 6 * S, pad.Y + 4 * S, 11, Muted);
        }
        else Text(dc, "Jiroskop / touchpad yalnız DualSense'te (HID) görünür.", tx, y + 10 * S, 13, Muted);
        if (MenuHold > 0) Text(dc, $"Çıkılıyor… {Math.Max(0, 2 - MenuHold):0.0}", W - 20 * S, 14 * S, 16, Warn, true, TextAlignment.Right);
    }

    private void Stick(DrawingContext dc, string name, float x, float y, double cx, double cy)
    {
        double r = 60 * S;
        dc.DrawEllipse(Panel, Line, new Point(cx, cy), r, r);
        dc.DrawEllipse(Accent, null, new Point(cx + x * r, cy - y * r), 9 * S, 9 * S);
        Text(dc, $"{name}  {x:+0.00;-0.00} {y:+0.00;-0.00}", cx - r, cy + r + 6 * S, 12, Muted);
    }

    private void Bar(DrawingContext dc, string name, float v, double x, double y)
    {
        double h = 160 * S, w = 40 * S;
        dc.DrawRoundedRectangle(Panel, Line, new Rect(x, y, w, h), 6, 6);
        dc.DrawRoundedRectangle(Accent, null, new Rect(x, y + h * (1 - v), w, h * v), 6, 6);
        Text(dc, $"{name}\n{v * 100:0}%", x + w / 2, y + h + 4 * S, 12, Muted, false, TextAlignment.Center);
    }

    private void DrawGuided(DrawingContext dc)
    {
        bool sony = Input?.Sony ?? false;
        string A = sony ? "✕" : "A", B = sony ? "○" : "B", X = sony ? "□" : "X", Y = sony ? "△" : "Y";
        Text(dc, "Geri bildirim testi", 20 * S, 14 * S, 22, Brushes.White, true);
        Text(dc, $"▲▼ seç · {A} çal · sonra {A} hissettim / {B} hissetmedim / {X} tekrar / {Y} atla · ☰ menüye dön. Seçili olmayan çıkışlar \"çıkış kapalı\" diye geçilir.",
            20 * S, 46 * S, 13, Muted, false, TextAlignment.Left, W - 40 * S);
        double y = 80 * S, rowH = Math.Min(26 * S, (H - 200 * S) / FeedbackTests.All.Length);
        for (int i = 0; i < FeedbackTests.All.Length; i++)
        {
            var t = FeedbackTests.All[i];
            var ans = Answers.GetValueOrDefault(t.Id);
            bool sel = i == GuidedIndex;
            if (sel) dc.DrawRoundedRectangle(Panel, null, new Rect(14 * S, y - 2, W * .55, rowH), 6, 6);
            bool available = Fb != null && (Fb.Enabled & t.Needs) != 0;
            var (mark, brush) = ans switch
            {
                TestAnswer.Yes => ("✓", Good), TestAnswer.No => ("✗", Bad), TestAnswer.Skipped => ("–", Muted),
                TestAnswer.NotAvailable => ("çıkış kapalı", Muted), _ => (available ? "" : "çıkış kapalı", Muted)
            };
            Text(dc, $"{i + 1,2}. {t.Title}", 24 * S, y, 14, sel ? Brushes.White : available ? Ink : Muted, sel);
            Text(dc, mark, W * .55, y, 14, brush, true, TextAlignment.Right);
            y += rowH;
        }
        var cur = FeedbackTests.All[GuidedIndex];
        double px = W * .6, pw = W * .38;
        dc.DrawRoundedRectangle(Panel, Line, new Rect(px, 80 * S, pw, 260 * S), 10, 10);
        Text(dc, cur.Title, px + 16 * S, 92 * S, 18, Brushes.White, true, TextAlignment.Left, pw - 32 * S);
        string phase = GuidedPhase switch
        {
            "playing" => $"Çalıyor… {Math.Max(0, cur.Seconds - GuidedTime):0.0} sn",
            "asking" => $"{cur.Ask}\n\n{A} evet   {B} hayır   {X} tekrar   {Y} atla",
            _ => $"{A} ile çal."
        };
        Text(dc, phase, px + 16 * S, 140 * S, 15, GuidedPhase == "asking" ? Warn : Ink, false, TextAlignment.Left, pw - 32 * S);
        int yes = Answers.Values.Count(a => a == TestAnswer.Yes), no = Answers.Values.Count(a => a == TestAnswer.No);
        Text(dc, $"Sonuç: {yes} evet · {no} hayır · {Answers.Count}/{FeedbackTests.All.Length} yanıtlandı", px + 16 * S, 300 * S, 14, Muted);
    }
}
