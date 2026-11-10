using Windows.Gaming.Input; using Windows.Gaming.Input.Custom;
// BLIND LOCALISATION TEST (Xbox mode). Each trial plays one hidden stimulus for 1.5 s:
// - a Sensa body part (216, 116, 249, 149) through the Razer GIP tunnel, at a random pitch/strength;
// - a GameInput reference motor (big, small, LT, RT);
// - or nothing.
// The user answers where it was felt. Stimulus and answer go to result.txt only. Silence frames between trials and at exit.
var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "result.txt")) { AutoFlush = true };
void W(string s) { lock (log) log.WriteLine(s); }
GipGameControllerProvider provider = null; byte tx = 0x30;
byte[] Frame(int body, int strength, int pitch)
{
    var bits = new List<int>();
    void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add((v >> i) & 1); }
    Put(0b0111100, 7);
    for (int b = 0; b < 4; b++)
    {
        int s = b == body ? strength : 0, p = b == body ? pitch : 6;
        Put(1, 1); Put(s, 6); Put(p, 7); Put(s, 6); Put(p, 7); Put(0, 1); Put(0, 1);
    }
    var f = new byte[(bits.Count + 7) / 8];
    for (int i = 0; i < bits.Count; i++) if (bits[i] == 1) f[i >> 3] |= (byte)(0x80 >> (i & 7));
    return f;
}
void Send(byte[] frame)
{
    if (provider == null) return;
    var pkt = new byte[60]; pkt[1] = tx++; pkt[5] = (byte)(frame.Length + 1); pkt[6] = 0x16; pkt[7] = 0x0E; pkt[8] = 0x01;
    frame.CopyTo(pkt, 9); byte x = 0; for (int i = 2; i < 58; i++) x ^= pkt[i]; pkt[58] = x;
    provider.SendMessage(GipMessageClass.Command, 0x0F, pkt);
}
var silence = Frame(-1, 0, 6);
IntPtr giDevice = IntPtr.Zero;
unsafe void GiFind()
{
    GameInputCreate(out IntPtr gi);
    var getCurrent = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr*, int>)(*(IntPtr**)gi)[4];
    for (int i = 0; i < 40 && giDevice == IntPtr.Zero; i++)
    {
        IntPtr rd;
        if (getCurrent(gi, 0x40000, IntPtr.Zero, &rd) == 0)
        {
            IntPtr d; ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)(*(IntPtr**)rd)[6])(rd, &d);
            IntPtr info = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(IntPtr**)d)[3])(d);
            if (*(ushort*)(info + 4) == 0x1532) giDevice = d;
        }
        else Thread.Sleep(100);
    }
}
unsafe void Rumble(float low, float high, float lt, float rt)
{
    if (giDevice == IntPtr.Zero) return;
    var r = new R { Low = low, High = high, LT = lt, RT = rt };
    ((delegate* unmanaged[Stdcall]<IntPtr, R*, void>)(*(IntPtr**)giDevice)[10])(giDevice, &r);
}

// Stimuli. Palm pitch p = (Hz-30)*127/370. 249/149 pitch is raw (meaning unknown). Every item twice, shuffled.
var stimuli = new List<Stim>();
foreach (var hz in new[] { 60, 120, 200 })
{
    int p = (int)Math.Round((hz - 30) * 127 / 370.0);
    stimuli.Add(new($"216 palmL {hz}Hz s48", 0, 48, p, null)); stimuli.Add(new($"116 palmR {hz}Hz s48", 1, 48, p, null));
}
foreach (var (s, p) in new[] { (40, 20), (63, 60), (63, 110) })
{
    stimuli.Add(new($"249 s{s} p{p}", 2, s, p, null)); stimuli.Add(new($"149 s{s} p{p}", 3, s, p, null));
}
stimuli.Add(new("REF big body", -1, 0, 0, new[] { .6f, 0, 0, 0 }));
stimuli.Add(new("REF small body", -1, 0, 0, new[] { 0, .6f, 0, 0 }));
stimuli.Add(new("REF left trigger", -1, 0, 0, new[] { 0, 0, .6f, 0 }));
stimuli.Add(new("REF right trigger", -1, 0, 0, new[] { 0, 0, 0, .6f }));
stimuli.Add(new("NONE", -1, 0, 0, null));
var rng = new Random();
var trials = stimuli.Concat(stimuli).OrderBy(_ => rng.Next()).ToList();

string[] answers = { "Sol avuç", "Sağ avuç", "Sol tetik", "Sağ tetik", "Sol tutamak (rumble)", "Sağ tutamak (rumble)", "Başka yer", "Hiçbir şey" };
var form = new Form { Text = "RW3 kör test", TopMost = true, Width = 980, Height = 420, StartPosition = FormStartPosition.CenterScreen, KeyPreview = true };
var label = new Label { Dock = DockStyle.Top, Height = 110, Font = new Font("Segoe UI", 22, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "Hazırlanıyor..." };
var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
form.Controls.Add(panel); form.Controls.Add(label);
TaskCompletionSource<int> tcs = null; var buttons = new List<Button>();
for (int i = 0; i < answers.Length; i++)
{
    int k = i;
    var b = new Button { Text = $"{i + 1}  {answers[i]}", Width = 225, Height = 70, Font = new Font("Segoe UI", 12), Enabled = false };
    b.Click += (_, _) => tcs?.TrySetResult(k); buttons.Add(b); panel.Controls.Add(b);
}
var replay = new Button { Text = "R  Tekrar çal", Width = 225, Height = 70, Font = new Font("Segoe UI", 12), Enabled = false };
replay.Click += (_, _) => tcs?.TrySetResult(-1); panel.Controls.Add(replay);
form.KeyDown += (_, e) =>
{
    if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D8) tcs?.TrySetResult(e.KeyCode - Keys.D1);
    else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad8) tcs?.TrySetResult(e.KeyCode - Keys.NumPad1);
    else if (e.KeyCode == Keys.R) tcs?.TrySetResult(-1);
};
void Enable(bool on) { foreach (var b in buttons) b.Enabled = on; replay.Enabled = on; }
async Task Play(Stim t, double sec)
{
    var f = t.Body >= 0 ? Frame(t.Body, t.S, t.P) : silence;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < sec)
    {
        Send(f);
        if (t.Rumble != null) Rumble(t.Rumble[0], t.Rumble[1], t.Rumble[2], t.Rumble[3]);
        await Task.Delay(33);
    }
    Rumble(0, 0, 0, 0);
    for (int i = 0; i < 4; i++) { Send(silence); await Task.Delay(33); }
}
form.Shown += async (_, _) =>
{
    form.Activate();
    try
    {
        GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(new Factory(p => provider = p), new Guid("AAE22FB7-E167-4908-9A40-FF9C687FA986"));
        for (int i = 0; i < 30 && provider == null; i++) await Task.Delay(100);
        GiFind();
        W($"{DateTime.Now:o} provider {provider != null} gameinput {giDevice != IntPtr.Zero} trials {trials.Count}");
        if (provider == null) { label.Text = "Kol bulunamadı (Xbox modu?)"; return; }
        for (int n = 0; n < trials.Count; n++)
        {
            var t = trials[n];
            Enable(false); label.Text = $"Deneme {n + 1}/{trials.Count}: dikkat..."; label.Refresh();
            for (int i = 0; i < 25; i++) { Send(silence); await Task.Delay(40); }
            label.Text = $"Deneme {n + 1}/{trials.Count}: ÇALIYOR"; label.Refresh();
            int replays = 0, ans;
            while (true)
            {
                await Play(t, 1.5);
                label.Text = $"Deneme {n + 1}/{trials.Count}: nerede hissettin? (1-8, R = tekrar)"; Enable(true);
                tcs = new TaskCompletionSource<int>(); ans = await tcs.Task; Enable(false);
                if (ans >= 0) break;
                replays++; label.Text = $"Deneme {n + 1}/{trials.Count}: tekrar ÇALIYOR"; label.Refresh();
            }
            W($"{n + 1}\t{t.Id}\t{answers[ans]}\treplays {replays}");
        }
        label.Text = "Bitti, teşekkürler! Pencereyi kapatabilirsin.";
        W("done");
    }
    catch (Exception ex) { W("ERR " + ex); label.Text = "Hata: " + ex.Message; }
    finally { Rumble(0, 0, 0, 0); for (int i = 0; i < 10; i++) { Send(silence); await Task.Delay(33); } }
};
form.FormClosing += (_, _) => { try { Rumble(0, 0, 0, 0); for (int i = 0; i < 5; i++) Send(silence); } catch { } };
Application.Run(form);

[System.Runtime.InteropServices.DllImport("GameInput.dll")] static extern int GameInputCreate(out IntPtr gi);
record Stim(string Id, int Body, int S, int P, float[] Rumble);
struct R { public float Low, High, LT, RT; }
sealed class Factory(Action<GipGameControllerProvider> keep) : ICustomGameControllerFactory
{
    public object CreateGameController(IGameControllerProvider p) { keep((GipGameControllerProvider)p); return new Sink(); }
    public void OnGameControllerAdded(IGameController v) { }
    public void OnGameControllerRemoved(IGameController v) { }
}
sealed class Sink : IGipGameControllerInputSink
{
    public void OnInputResumed(ulong t) { }
    public void OnInputSuspended(ulong t) { }
    public void OnKeyReceived(ulong t, byte k, bool p) { }
    public void OnMessageReceived(ulong t, GipMessageClass c, byte id, byte seq, byte[] b) { }
}
