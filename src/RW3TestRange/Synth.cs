namespace RW3TestRange;

/// <summary>
/// Every sound and haptic waveform of the game, generated in code (no audio files). Stereo interleaved float at
/// 48 kHz. Speaker sounds are full band; haptic waveforms (DualSense channels 3/4) stay in the actuators' useful
/// 30–400 Hz range, as game haptics do.
/// </summary>
public static class Synth
{
    public const int Rate = 48000;
    private static readonly Random Rng = new(7);

    private static float[] Buf(double seconds) => new float[(int)(seconds * Rate) * 2];
    private static float Noise() => (float)(Rng.NextDouble() * 2 - 1);

    /// <summary>pan -1 = left .. +1 = right (equal power).</summary>
    private static (float L, float R) Pan(float pan)
    {
        double a = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    private static void Add(float[] b, int i, float v, float pan)
    {
        var (l, r) = Pan(pan);
        b[2 * i] += v * l; b[2 * i + 1] += v * r;
    }

    // ───────────── speaker sounds ─────────────

    public static float[] Shot(int weapon, float pan = .2f)
    {
        double len = weapon switch { 2 => .55, 3 => .35, 1 => .18, _ => .3 };
        var b = Buf(len); int n = b.Length / 2;
        double body = weapon switch { 2 => 55, 1 => 95, 3 => 140, _ => 80 };
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            if (weapon == 3)   // bow: string twang
            {
                Add(b, i, (float)(Math.Sin(2 * Math.PI * 220 * t) * Math.Exp(-t * 18) * .5 + Math.Sin(2 * Math.PI * 440 * t) * Math.Exp(-t * 30) * .2), pan);
                continue;
            }
            lp += (Noise() - lp) * (weapon == 2 ? .25f : .45f);
            float crack = lp * (float)Math.Exp(-t * (weapon == 1 ? 45 : 28));
            float thump = (float)(Math.Sin(2 * Math.PI * body * t * (1 - t * .6)) * Math.Exp(-t * 14));
            Add(b, i, crack * .8f + thump * .7f, pan);
        }
        return b;
    }

    public static float[] Shatter(float pan)
    {
        var b = Buf(.7); int n = b.Length / 2;
        var tinkles = Enumerable.Range(0, 14).Select(_ => (at: Rng.NextDouble() * .45, hz: 2500 + Rng.NextDouble() * 4500, p: (float)(pan + (Rng.NextDouble() - .5) * .4))).ToArray();
        float hp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            float x = Noise(); hp = .9f * (hp + x - prev); prev = x;
            float v = hp * (float)Math.Exp(-t * 9) * .6f;
            foreach (var k in tinkles)
                if (t >= k.at) v += (float)(Math.Sin(2 * Math.PI * k.hz * (t - k.at)) * Math.Exp(-(t - k.at) * 40) * .18);
            Add(b, i, v, pan);
        }
        return b;
    }

    public static float[] Click(double hz = 1800, double len = .05, float gain = .5f)
    {
        var b = Buf(len); int n = b.Length / 2;
        for (int i = 0; i < n; i++) { double t = i / (double)Rate; Add(b, i, (float)(Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t * 90)) * gain, 0); }
        return b;
    }

    public static float[] Reload()
    {
        var a = Click(1400, .06, .5f); var c = Click(900, .08, .6f);
        var b = Buf(.45);
        a.CopyTo(b, 0); for (int i = 0; i < c.Length; i++) b[(int)(.28 * Rate) * 2 + i] += c[i];
        return b;
    }

    public static float[] Explosion(float pan)
    {
        var b = Buf(1.8); int n = b.Length / 2; float lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            lp += (Noise() - lp) * .06f;
            float boom = (float)(Math.Sin(2 * Math.PI * (45 - t * 12) * t) * Math.Exp(-t * 2.5));
            Add(b, i, (lp * 3f * (float)Math.Exp(-t * 2.2) + boom * .8f) * .8f, pan);
        }
        return b;
    }

    public static float[] Heartbeat()
    {
        var b = Buf(.8); int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, v = 0;
            foreach (double at in new[] { 0, .22 }) if (t >= at) v += Math.Sin(2 * Math.PI * 55 * (t - at)) * Math.Exp(-(t - at) * 22);
            Add(b, i, (float)v * .6f, 0);
        }
        return b;
    }

    /// <summary>Speaker channel test: a short tone on one side only.</summary>
    public static float[] Tone(double hz, double seconds, float pan, float gain = .35f)
    {
        var b = Buf(seconds); int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, env = Math.Min(1, Math.Min(t / .02, (seconds - t) / .05));
            Add(b, i, (float)(Math.Sin(2 * Math.PI * hz * t) * env) * gain, pan);
        }
        return b;
    }

    // ───────────── haptic waveforms (DualSense actuators, channels 3/4) ─────────────

    /// <summary>A decaying low-frequency punch: the basic haptic "hit".</summary>
    public static float[] HPunch(double hz, double seconds, float pan, float gain = .9f, double decay = 18)
    {
        var b = Buf(seconds); int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            Add(b, i, (float)(Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t * decay)) * gain, pan);
        }
        return b;
    }

    public static float[] HShot(int weapon) => weapon switch
    {
        2 => Mix(HPunch(45, .35, 0, 1f, 9), HPunch(90, .12, .3f, .6f, 30)),
        1 => HPunch(120, .07, .4f, .8f, 45),
        3 => Mix(HPunch(180, .15, .5f, .6f, 25), HPunch(70, .1, 0, .5f, 35)),
        _ => Mix(HPunch(70, .18, .3f, .9f, 22), HPunch(160, .05, .5f, .4f, 60))
    };

    /// <summary>Glass: a burst of short high clicks (150–320 Hz) panned where the bottle was.</summary>
    public static float[] HShatter(float pan)
    {
        var b = Buf(.4);
        for (int k = 0; k < 9; k++)
        {
            int at = (int)(Rng.NextDouble() * .25 * Rate);
            var c = HPunch(150 + Rng.NextDouble() * 170, .05, pan + (float)(Rng.NextDouble() - .5) * .3f, .55f, 70);
            for (int i = 0; i < c.Length && 2 * at + i < b.Length; i++) b[2 * at + i] += c[i];
        }
        return b;
    }

    public static float[] HReload() => Mix(HPunch(200, .04, -.2f, .5f, 90), Shift(HPunch(140, .06, -.2f, .7f, 70), .28));

    public static float[] HExplosion(float pan)
    {
        var b = Buf(1.4); int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double v = Math.Sin(2 * Math.PI * 38 * t) * Math.Exp(-t * 2.2) + Math.Sin(2 * Math.PI * 75 * t) * Math.Exp(-t * 6) * .5;
            Add(b, i, (float)v * .9f, pan * .5f);
        }
        return b;
    }

    public static float[] HHeartbeat() => Mix(HPunch(50, .18, -.3f, .8f, 20), Shift(HPunch(45, .2, .3f, .6f, 18), .22));

    /// <summary>Bow string under tension: a soft 60 Hz hum; the game plays it in short pieces while drawing.</summary>
    public static float[] HTension(float amount) => HPunch(60 + 40 * amount, .12, .3f, .25f + .5f * amount, 4);

    public static float[] HTone(double hz, double seconds, float pan, float gain = .8f) => Tone(hz, seconds, pan, gain);

    /// <summary>120 Hz moving left → right → left: tests that left/right reach the right palms.</summary>
    public static float[] HSweep(double seconds = 3)
    {
        var b = Buf(seconds); int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            float pan = (float)Math.Sin(2 * Math.PI * t / seconds - Math.PI / 2);
            Add(b, i, (float)(Math.Sin(2 * Math.PI * 120 * t) * .8), pan);
        }
        return b;
    }

    /// <summary>A small tune in the actuators' range (C3..C4, 130–262 Hz): the palms should "sing" it.</summary>
    public static float[] HMelody()
    {
        double[] notes = { 130.8, 146.8, 164.8, 174.6, 196.0, 220.0, 246.9, 261.6, 196.0, 164.8, 130.8 };
        var b = Buf(notes.Length * .3);
        for (int k = 0; k < notes.Length; k++)
        {
            var tone = Tone(notes[k], .28, k % 2 == 0 ? -.4f : .4f, .8f);
            for (int i = 0; i < tone.Length; i++) b[(int)(k * .3 * Rate) * 2 + i] += tone[i];
        }
        return b;
    }

    // ───────────── helpers ─────────────

    public static float[] Mix(float[] a, float[] b)
    {
        var r = new float[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) r[i] += a[i];
        for (int i = 0; i < b.Length; i++) r[i] += b[i];
        return r;
    }

    public static float[] Shift(float[] a, double seconds)
    {
        int off = (int)(seconds * Rate) * 2;
        var r = new float[a.Length + off];
        a.CopyTo(r, off);
        return r;
    }
}

/// <summary>
/// Endless background music (no files): 112 BPM, A minor, bass + kick + hats + chord pad + a lead line.
/// Its bass and melody are what RW3's audio-to-haptics and melody mode should pick up.
/// </summary>
public sealed class MusicGenerator
{
    private const double Bpm = 112;
    private long _n;
    private float _lp;
    private readonly Random _rng = new(3);
    public float Volume = .5f;

    private static readonly int[] Bass = { 0, 0, 7, 0, 5, 5, 3, 7 };          // semitones from A1 per beat pair
    private static readonly int[][] Chords = { new[] { 0, 3, 7 }, new[] { 5, 8, 12 }, new[] { 3, 7, 10 }, new[] { 7, 10, 14 } };
    private static readonly int[] Lead = { 12, 15, 19, 17, 15, 12, 10, 12, 15, 17, 19, 22, 19, 17, 15, 12 };

    private static double Hz(double baseHz, int semis) => baseHz * Math.Pow(2, semis / 12.0);

    public void Read(float[] buffer, int offset, int frames)
    {
        double beat = 60.0 / Bpm;
        for (int f = 0; f < frames; f++, _n++)
        {
            double t = _n / (double)Synth.Rate;
            double b = t / beat;                       // beat position
            int beatIdx = (int)b; double inBeat = b - beatIdx;
            int bar = beatIdx / 4;
            // kick on every beat, snare-ish noise on 2 and 4, hats on 8ths
            double kick = Math.Sin(2 * Math.PI * 50 * inBeat * beat * (1 - inBeat * .5)) * Math.Exp(-inBeat * beat * 18);
            _lp += ((float)(_rng.NextDouble() * 2 - 1) - _lp) * .5f;
            double snare = beatIdx % 2 == 1 ? _lp * Math.Exp(-inBeat * beat * 22) * .5 : 0;
            double half = (b * 2) % 1;
            double hat = (_rng.NextDouble() * 2 - 1) * Math.Exp(-half * beat * .5 * 60) * .12;
            // bass: two notes per bar pair
            int bassSemi = Bass[(beatIdx / 2) % Bass.Length];
            double bassHz = Hz(55, bassSemi);
            double bass = (Math.Sin(2 * Math.PI * bassHz * t) * .7 + Math.Sin(2 * Math.PI * bassHz * 2 * t) * .2) * Math.Min(1, (1 - inBeat) * 3);
            // pad
            var ch = Chords[bar % Chords.Length]; double pad = 0;
            foreach (int s in ch) pad += Math.Sin(2 * Math.PI * Hz(220, s) * t) * .06;
            // lead: eighth notes, every other bar
            double lead = 0;
            if (bar % 2 == 1)
            {
                int step = (int)(b * 2) % Lead.Length; double inStep = half;
                lead = Math.Sin(2 * Math.PI * Hz(220, Lead[step]) * t) * Math.Exp(-inStep * 2.5) * .22;
            }
            double v = (kick * .8 + snare + hat + bass * .55 + pad + lead) * Volume * .7;
            float l = (float)(v + lead * .15 * Volume), r = (float)(v - lead * .15 * Volume);
            buffer[offset + 2 * f] += l; buffer[offset + 2 * f + 1] += r;
        }
    }
}
