using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

public sealed record GameSimScene(string Id, string Title, string Hint, double Seconds);

/// <summary>One output report a game would send at <see cref="At"/> seconds (data after report id 0x02).</summary>
public readonly record struct SimOutputStep(double At, byte[] Data);

public sealed record GameSimResult(bool AudioPlayed, int ReportsWritten, string? HidError, double? MicDb);

/// <summary>
/// Plays game-like DualSense signals into the virtual DualSense, the same two channels a game uses:
/// haptic PCM on the controller's 4-channel audio endpoint (channels 3/4) and output report 0x02
/// (adaptive-trigger effects, rumble) on its HID interface. Synthetic scenes, not game recordings;
/// they exercise the whole path (Windows audio → HIDMaestro → engine → Sensa, HID → trigger
/// interpreter → motors) without starting a game.
/// </summary>
public static class GameSignalSimulator
{
    public const int Rate = 48000;
    public const int OutputDataLength = 47;          // USB output report 0x02 is 48 bytes with its id

    public static readonly IReadOnlyList<GameSimScene> Scenes =
    [
        new("weapon", "Silah: R2 tetik + atışlar", "R2'yi yavaşça sonuna kadar çek: yolun ortasında tık; atışlar iki avuçta, sağ daha güçlü.", 4.6),
        new("bow", "Yay: L2 direnç, R2 titreşim", "L2'yi çek (kademeli tıklar), R2'yi yarıdan fazla çek (sürekli titreşim); yay gerilir ve bırakılır.", 4.2),
        new("steps", "Ayak sesleri: sol-sağ adımlar", "Tetik yok. Sol, sağ, sol… sırayla yumuşak adımlar.", 4.0),
        new("explosion", "Patlama + gövde titreşimi", "Güçlü vuruş ve sönen derin sarsıntı; aynı anda kolun klasik titreşim motorları.", 3.2),
        new("engine", "Araç motoru (80 + 190 Hz)", "İki elde karışık motor sesi; R2'yi çekince gaz titreşimi.", 3.6),
        new("rain", "Yağmur: rastgele tıkırtılar", "İki avuçta dağınık, hafif tıkırtılar.", 4.0),
        new("pan", "Sol → sağ → sol kayma", "120 Hz titreşim sol avuçtan sağa, sonra geri kayar.", 4.0),
        new("all", "Tam tur (hepsi art arda)", "Bütün sahneler sırayla, aralarında kısa sessizlik (~32 sn).", 0),
    ];

    public static GameSimScene Scene(string id) => Scenes.First(s => s.Id == id);

    // ───────────── haptic PCM (pure, deterministic) ─────────────

    public static (float[] L, float[] R) Haptics(string id)
    {
        if (id == "all")
        {
            var parts = Scenes.Where(s => s.Id != "all").Select(s => Haptics(s.Id)).ToList();
            int gap = (int)(0.6 * Rate);
            int total = parts.Sum(p => p.L.Length + gap);
            float[] l = new float[total], r = new float[total];
            int at = 0;
            foreach (var (pl, pr) in parts) { pl.CopyTo(l, at); pr.CopyTo(r, at); at += pl.Length + gap; }
            return (l, r);
        }
        var sc = Scene(id);
        int n = (int)(sc.Seconds * Rate);
        float[] L = new float[n], R = new float[n];
        var rng = new Random(7);
        switch (id)
        {
            case "weapon":
                foreach (double t in new[] { 0.6, 1.4, 2.2, 3.0, 3.8 })
                {
                    Click(L, t, 0.7); Click(R, t, 1.0);
                    Tone(L, t, 0.16, 110, 0.55, 0.002, 0.05); Tone(R, t, 0.16, 110, 0.8, 0.002, 0.05);
                }
                break;
            case "bow":
                Tone(L, 0.2, 2.6, 70, 0.45, 2.4, 0.05, rampUp: true); Tone(R, 0.2, 2.6, 70, 0.45, 2.4, 0.05, rampUp: true);
                Click(L, 3.0, 0.8); Click(R, 3.0, 0.8);
                Tone(L, 3.0, 0.35, 160, 0.6, 0.002, 0.1); Tone(R, 3.0, 0.35, 160, 0.6, 0.002, 0.1);
                break;
            case "steps":
                for (int i = 0; i < 8; i++)
                {
                    double t = 0.3 + i * 0.45; var b = i % 2 == 0 ? L : R;
                    Click(b, t, 0.35); Tone(b, t, 0.09, 60, 0.6, 0.004, 0.03);
                }
                break;
            case "explosion":
                Click(L, 0.4, 1.0); Click(R, 0.4, 1.0);
                Tone(L, 0.4, 1.6, 45, 0.9, 0.003, 0.45); Tone(R, 0.4, 1.6, 48, 0.9, 0.003, 0.45);
                for (int i = (int)(0.4 * Rate); i < (int)(1.4 * Rate) && i < n; i++)
                {
                    double env = Math.Exp(-(i / (double)Rate - 0.4) / 0.3) * 0.25;
                    L[i] += (float)((rng.NextDouble() * 2 - 1) * env); R[i] += (float)((rng.NextDouble() * 2 - 1) * env);
                }
                break;
            case "engine":
                for (int i = (int)(0.2 * Rate); i < (int)(3.4 * Rate); i++)
                {
                    double t = i / (double)Rate, trem = 0.85 + 0.15 * Math.Sin(2 * Math.PI * 7 * t);
                    double edge = Math.Clamp(Math.Min(t - 0.2, 3.4 - t) / 0.08, 0, 1);
                    float v = (float)(edge * trem * (0.38 * Math.Sin(2 * Math.PI * 80 * t) + 0.32 * Math.Sin(2 * Math.PI * 190 * t)));
                    L[i] += v; R[i] += v;
                }
                break;
            case "rain":
                for (int i = 0; i < 100; i++)
                {
                    double t = 0.2 + rng.NextDouble() * 3.6;
                    var b = rng.Next(2) == 0 ? L : R;
                    Tone(b, t, 0.008, 200, 0.25 + rng.NextDouble() * 0.25, 0.0005, 0.003);
                }
                break;
            case "pan":
                for (int i = (int)(0.2 * Rate); i < (int)(3.8 * Rate); i++)
                {
                    double t = i / (double)Rate, pos = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (t - 0.2) / 3.6);   // 0 → 1 → 0
                    double edge = Math.Clamp(Math.Min(t - 0.2, 3.8 - t) / 0.05, 0, 1);
                    double s = Math.Sin(2 * Math.PI * 120 * t) * 0.7 * edge;
                    L[i] += (float)(s * Math.Cos(pos * Math.PI / 2)); R[i] += (float)(s * Math.Sin(pos * Math.PI / 2));
                }
                break;
        }
        for (int i = 0; i < n; i++) { L[i] = Math.Clamp(L[i], -1f, 1f); R[i] = Math.Clamp(R[i], -1f, 1f); }
        return (L, R);
    }

    private static void Click(float[] b, double at, double amp) => Tone(b, at, 0.006, 250, amp, 0.0003, 0.002);

    /// <summary>Sine burst: attack (or a linear ramp-up over the whole tone), exponential decay.</summary>
    private static void Tone(float[] b, double at, double dur, double hz, double amp, double attack, double decay, bool rampUp = false)
    {
        int s = (int)(at * Rate), e = Math.Min(b.Length, (int)((at + dur) * Rate));
        for (int i = Math.Max(0, s); i < e; i++)
        {
            double t = (i - s) / (double)Rate;
            double env = rampUp ? Math.Min(1, t / attack) * Math.Clamp((dur - t) / decay, 0, 1)
                                : Math.Min(1, t / attack) * Math.Exp(-t / decay);
            b[i] += (float)(amp * env * Math.Sin(2 * Math.PI * hz * t));
        }
    }

    // ───────────── output reports (pure) ─────────────

    public static readonly TriggerEffect Off = new(0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Weapon (0x25): resistance from zone <paramref name="start"/>, "breaks" after zone <paramref name="end"/>.</summary>
    public static TriggerEffect Weapon(int start, int end, int strength) =>
        new(0x25, (byte)((1 << start) | (1 << end)), (byte)(((1 << start) | (1 << end)) >> 8), (byte)Math.Clamp(strength - 1, 0, 7), 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Zone effect with a 10-zone mask and 3-bit strengths: feedback (0x21) or vibration (0x26).</summary>
    public static TriggerEffect Zones(byte mode, int fromZone, int strength, byte frequency = 0)
    {
        int mask = 0; ulong packed = 0;
        for (int z = fromZone; z < 10; z++) { mask |= 1 << z; packed |= (ulong)Math.Clamp(strength - 1, 0, 7) << (3 * z); }
        return new(mode, (byte)mask, (byte)(mask >> 8), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24), 0, 0, frequency, 0);
    }

    /// <summary>Report 0x02 data (after the id) as a game builds it.</summary>
    public static byte[] OutputData(TriggerEffect? right, TriggerEffect? left, byte? rumbleLeft = null, byte? rumbleRight = null)
    {
        var d = new byte[OutputDataLength];
        if (rumbleLeft != null || rumbleRight != null) { d[0] |= 0x03; d[2] = rumbleRight ?? 0; d[3] = rumbleLeft ?? 0; }
        if (right is { } r) { d[0] |= 0x04; Write(d, 10, r); }
        if (left is { } l) { d[0] |= 0x08; Write(d, 21, l); }
        return d;
    }

    private static void Write(byte[] d, int at, in TriggerEffect e)
    {
        d[at] = e.Mode; d[at + 1] = e.P0; d[at + 2] = e.P1; d[at + 3] = e.P2; d[at + 4] = e.P3; d[at + 5] = e.P4;
        d[at + 6] = e.P5; d[at + 7] = e.P6; d[at + 8] = e.P7; d[at + 9] = e.P8; d[at + 10] = e.P9;
    }

    public static List<SimOutputStep> Outputs(string id)
    {
        var steps = new List<SimOutputStep>();
        if (id == "all")
        {
            double at = 0;
            foreach (var s in Scenes.Where(s => s.Id != "all"))
            {
                steps.AddRange(Outputs(s.Id).Select(x => x with { At = x.At + at }));
                at += s.Seconds + 0.6;
            }
            return steps;
        }
        var sc = Scene(id);
        switch (id)
        {
            case "weapon": steps.Add(new(0, OutputData(Weapon(3, 7, 6), Off))); break;
            case "bow": steps.Add(new(0, OutputData(Zones(0x26, 4, 6, 30), Zones(0x21, 2, 5)))); break;
            case "engine": steps.Add(new(0, OutputData(Zones(0x26, 1, 4, 40), Off))); break;
            case "explosion":
                steps.Add(new(0, OutputData(Off, Off, 0, 0)));
                steps.Add(new(0.4, OutputData(Off, Off, 255, 200)));
                steps.Add(new(1.2, OutputData(Off, Off, 0, 0)));
                break;
            default: steps.Add(new(0, OutputData(Off, Off))); break;
        }
        steps.Add(new(sc.Seconds, OutputData(Off, Off, 0, 0)));   // like a game leaving the scene
        return steps;
    }

    public static double Duration(string id) => id == "all" ? Scenes.Where(s => s.Id != "all").Sum(s => s.Seconds + 0.6) : Scene(id).Seconds;

    // ───────────── runtime ─────────────

    /// <summary>The virtual (or physical) DualSense's 4-channel render endpoint.</summary>
    public static string? FindEndpoint()
    {
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (d)
                if ((d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase))
                    && d.AudioClient.MixFormat.Channels == 4)
                    return d.ID;
        return null;
    }

    public static async Task<GameSimResult> PlayAsync(string sceneId, string endpointId, string? hidPath, string? micId, CancellationToken token)
    {
        var (l, r) = Haptics(sceneId);
        var steps = Outputs(sceneId);
        double seconds = l.Length / (double)Rate;

        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDevice(endpointId);
        var fmt = dev.AudioClient.MixFormat;
        if (fmt.Channels != 4) throw new InvalidOperationException("DualSense uç noktası 4 kanallı değil: " + fmt);
        byte[] pcm = Interleave(l, r, fmt);

        using var mic = micId != null ? new MicLevel(micId) : null;
        double? baseline = null;
        if (mic != null) { await Task.Delay(300, token); mic.Take(); await Task.Delay(900, token); baseline = mic.Take(); }

        SafeFileHandle? hid = null; string? hidError = null; int outLen = 0, written = 0;
        if (hidPath != null)
        {
            hid = SensaHidTransport.Hid.CreateFile(hidPath, SensaHidTransport.Hid.GenericWrite, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hid.IsInvalid) { hidError = "HID açılamadı (" + Marshal.GetLastWin32Error() + ")"; hid.Dispose(); hid = null; }
            else outLen = Math.Max(SensaHidTransport.Hid.OutputLength(hid), OutputDataLength + 1);
        }

        try
        {
            using var ms = new MemoryStream(pcm, false);
            using var src = new RawSourceWaveStream(ms, fmt);
            using var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
            output.Init(src);
            var clock = Stopwatch.StartNew();
            output.Play();
            int next = 0; byte[]? current = null; double lastSend = -1;
            if (mic != null) mic.Take();
            while (clock.Elapsed.TotalSeconds < seconds + 0.25)
            {
                token.ThrowIfCancellationRequested();
                double t = clock.Elapsed.TotalSeconds;
                bool changed = false;
                while (next < steps.Count && steps[next].At <= t) { current = steps[next++].Data; changed = true; }
                // Games resend their output state continuously; every 100 ms here.
                if (hid != null && current != null && (changed || t - lastSend >= 0.1))
                {
                    var buf = new byte[outLen];
                    buf[0] = DualSenseOutputReport.ReportId;
                    current.AsSpan(0, Math.Min(current.Length, outLen - 1)).CopyTo(buf.AsSpan(1));
                    if (WriteFile(hid, buf, buf.Length, out _, IntPtr.Zero)) written++;
                    else hidError ??= "HID yazılamadı (" + Marshal.GetLastWin32Error() + ")";
                    lastSend = t;
                }
                await Task.Delay(15, token);
            }
            output.Stop();
            double? db = null;
            if (mic != null && baseline is > 0) db = 20 * Math.Log10(Math.Max(mic.Take(), 1e-9) / baseline.Value);
            return new GameSimResult(true, written, hidError, db);
        }
        finally
        {
            if (hid != null)
            {
                var off = new byte[outLen]; off[0] = DualSenseOutputReport.ReportId;
                OutputData(Off, Off, 0, 0).AsSpan(0, Math.Min(OutputDataLength, outLen - 1)).CopyTo(off.AsSpan(1));
                WriteFile(hid, off, off.Length, out _, IntPtr.Zero);
                hid.Dispose();
            }
        }
    }

    /// <summary>4-channel frames in the endpoint's mix format; haptics on channels 3/4, speakers silent.</summary>
    public static byte[] Interleave(float[] l, float[] r, WaveFormat fmt)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
            (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        int bytes = fmt.BitsPerSample / 8, frame = bytes * 4;
        var pcm = new byte[l.Length * frame];
        for (int i = 0; i < l.Length; i++)
        {
            if (isFloat && bytes == 4)
            {
                BitConverter.TryWriteBytes(pcm.AsSpan(i * frame + 2 * bytes, 4), l[i]);
                BitConverter.TryWriteBytes(pcm.AsSpan(i * frame + 3 * bytes, 4), r[i]);
            }
            else if (!isFloat && bytes == 2)
            {
                BitConverter.TryWriteBytes(pcm.AsSpan(i * frame + 2 * bytes, 2), (short)Math.Round(l[i] * 32767));
                BitConverter.TryWriteBytes(pcm.AsSpan(i * frame + 3 * bytes, 2), (short)Math.Round(r[i] * 32767));
            }
            else throw new InvalidOperationException("Desteklenmeyen uç nokta biçimi: " + fmt);
        }
        return pcm;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int toWrite, out int written, IntPtr overlapped);

    /// <summary>RMS level of a capture endpoint (the webcam mic used as vibration sensor). Nothing is stored.</summary>
    private sealed class MicLevel : IDisposable
    {
        private readonly MMDeviceEnumerator _en = new();
        private readonly MMDevice _dev;
        private readonly WasapiCapture _cap;
        private double _sum; private long _n;
        private readonly object _g = new();

        public MicLevel(string id)
        {
            _dev = _en.GetDevice(id);
            _cap = new WasapiCapture(_dev);
            var f = _cap.WaveFormat;
            _cap.DataAvailable += (_, e) =>
            {
                lock (_g)
                {
                    bool fl = f.Encoding == WaveFormatEncoding.IeeeFloat || f.BitsPerSample == 32;
                    int step = (fl ? 4 : 2) * f.Channels;
                    for (int i = 0; i + step <= e.BytesRecorded; i += step)
                    {
                        double v = fl ? BitConverter.ToSingle(e.Buffer, i) : BitConverter.ToInt16(e.Buffer, i) / 32768.0;
                        _sum += v * v; _n++;
                    }
                }
            };
            _cap.StartRecording();
        }

        public double Take() { lock (_g) { double r = _n > 0 ? Math.Sqrt(_sum / _n) : 0; _sum = 0; _n = 0; return r; } }

        public void Dispose()
        {
            try { _cap.StopRecording(); } catch { }
            _cap.Dispose(); _dev.Dispose(); _en.Dispose();
        }
    }
}

/// <summary>Turns the simulator's before/after counters into a short verdict per stage of the path.</summary>
public static class GameSimVerdict
{
    public const float SilentPeak = 0.02f;
    public const double FeltDb = 3.0;

    public static string Describe(long hapticBlocks, float peakL, float peakR, long sensaFrames,
        int reportsWritten, long triggerReports, string? hidError, double? micDb)
    {
        var lines = new System.Collections.Generic.List<string>();
        bool audio = hapticBlocks > 0 && Math.Max(peakL, peakR) >= SilentPeak;
        if (hapticBlocks == 0) lines.Add("✗ Sanal DualSense titreşim sesini almadı (Windows ses yolu).");
        else if (!audio) lines.Add($"✗ Titreşim sesi sessiz geldi (tepe L {peakL:F2} R {peakR:F2}).");
        else lines.Add($"✓ Titreşim sesi köprüde: {hapticBlocks} blok, tepe L {peakL:F2} R {peakR:F2}.");
        if (audio)
            lines.Add(sensaFrames > 0 ? $"✓ Sensa'ya {sensaFrames} kare gitti."
                                      : "✗ Sensa'ya hiç kare gitmedi: motor (Sensa) tarafı çalışmıyor.");
        if (hidError != null) lines.Add("✗ Tetik komutu yazılamadı: " + hidError);
        else if (reportsWritten > 0)
            lines.Add(triggerReports > 0 ? $"✓ Tetik komutları köprüde ({triggerReports})." : "✗ Tetik komutları köprüye ulaşmadı.");
        if (micDb is double db) lines.Add((db >= FeltDb ? "✓" : "✗") + $" Mikrofon: {db:+0.0;-0.0} dB");
        return string.Join(Environment.NewLine, lines);
    }
}
