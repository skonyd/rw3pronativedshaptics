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

    // ───────────── speaker sounds (layered, filtered, with a short room) ─────────────

    private static readonly Dictionary<string, float[][]> Cache = new();

    /// <summary>A few pre-rendered variants per sound, chosen at random so repeats do not sound identical.</summary>
    private static float[] Variant(string key, int count, Func<int, float[]> make)
    {
        float[][] v;
        lock (Cache)
            if (!Cache.TryGetValue(key, out v!)) Cache[key] = v = Enumerable.Range(0, count).Select(make).ToArray();
        return v[Random.Shared.Next(v.Length)];
    }

    /// <summary>Gunshot: transient crack + filtered body + low thump (+ string for bow / crossbow), then a room tail.</summary>
    public static float[] Shot(int weapon, float pan = .2f)
    {
        var mono = Variant("shot" + weapon, 3, seed => weapon switch
        {
            1 => Gun(seed, .28, crackHz: 2600, bodyHz: 1100, bodyDecay: 30, thumpFrom: 170, thumpTo: 70, thumpGain: .6f, tail: 14),
            2 => Gun(seed, .7, crackHz: 1500, bodyHz: 700, bodyDecay: 9, thumpFrom: 120, thumpTo: 42, thumpGain: 1.1f, tail: 6),
            3 => Bow(seed),
            4 => Crossbow(seed),
            _ => Gun(seed, .45, crackHz: 2100, bodyHz: 900, bodyDecay: 18, thumpFrom: 150, thumpTo: 55, thumpGain: .8f, tail: 9)
        });
        return Dsp.Place(mono, pan, weapon is 3 or 4 ? .12f : .28f, weapon == 2 ? 1.25 : 1, weapon == 2 ? .9 : .6);
    }

    private static float[] Gun(int seed, double len, double crackHz, double bodyHz, double bodyDecay, double thumpFrom, double thumpTo, float thumpGain, double tail)
    {
        int n = Dsp.N(len);
        var crack = Dsp.Decay(Dsp.Filter(Dsp.Noise(n, seed * 7 + 1), Biquad.HighPass(crackHz)), 180);
        var body = Dsp.Decay(Dsp.Filter(Dsp.Noise(n, seed * 7 + 2), Biquad.BandPass(bodyHz, .7)), bodyDecay);
        var low = Dsp.Decay(Dsp.Filter(Dsp.Noise(n, seed * 7 + 3), Biquad.LowPass(400)), tail);
        var thump = Dsp.Sweep(len, thumpFrom, thumpTo, .04, 14);
        var mix = Dsp.Add(Dsp.Add(Dsp.Add(Dsp.Gain(crack, .7f), body, 1.1f), low, .9f), thump, thumpGain);
        return Dsp.Finish(mix, .95f);
    }

    private static float[] Bow(int seed)
    {
        var twang = Dsp.Decay(Dsp.String(196 + seed * 4, .6, .6, seed + 11), 7);
        var swish = new float[Dsp.N(.3)]; var noise = Dsp.Noise(swish.Length, seed + 21); var bp = Biquad.BandPass(1400, 1.5);
        for (int i = 0; i < swish.Length; i++) { double t = i / (double)Rate; swish[i] = bp.Process(noise[i]) * (float)(Math.Sin(Math.PI * t / .3) * .8); }
        var thud = Dsp.Sweep(.12, 160, 90, .02, 40);
        return Dsp.Finish(Dsp.Add(Dsp.Add(twang, swish, .6f, .02), thud, .5f), .85f);
    }

    private static float[] Crossbow(int seed)
    {
        var latch = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.05), seed + 31), Biquad.BandPass(2800, 3)), 120);
        var twang = Dsp.Decay(Dsp.String(110 + seed * 3, .5, .35, seed + 41, .993), 9);
        var thump = Dsp.Sweep(.25, 130, 50, .03, 16);
        return Dsp.Finish(Dsp.Add(Dsp.Add(Dsp.Gain(latch, .8f), twang, 1f, .008), thump, .9f), .95f);
    }

    /// <summary>Glass: a bright impact, then shards (narrow resonant noise bursts at random high pitches) falling over ~0.4 s.</summary>
    public static float[] Shatter(float pan)
    {
        var mono = Variant("glass", 4, seed =>
        {
            var r = new Random(seed + 100);
            int n = Dsp.N(.8);
            var impact = Dsp.Decay(Dsp.Filter(Dsp.Noise(n, seed + 101), Biquad.HighPass(1800)), 28);
            var mix = Dsp.Gain(impact, .8f);
            for (int k = 0; k < 26; k++)
            {
                double at = Math.Pow(r.NextDouble(), 1.8) * .45;     // denser at the start
                double hz = 2800 + r.NextDouble() * 6500;
                var ping = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.09), seed * 31 + k), Biquad.BandPass(hz, 18)), 35 + r.NextDouble() * 40);
                mix = Dsp.Add(mix, ping, (float)(3.5 * (1 - at)), at);
            }
            var clink = Dsp.Decay(Dsp.Sweep(.25, 4200, 4100, 1, 18), 0);
            return Dsp.Finish(Dsp.Add(mix, clink, .25f, .03), .8f);
        });
        return Dsp.Place(mono, pan, .3f, .8, .5);
    }

    /// <summary>Renders every sound once (background), so the first shot does not stall a frame.</summary>
    public static void Warm()
    {
        for (int w = 0; w < 5; w++) Shot(w);
        Shatter(0); Reload(); Explosion(0); Heartbeat(); Melee();
        foreach (var hz in new[] { 1200.0, 1100, 2600, 500 }) Click(hz);
    }

    /// <summary>Short mechanical click (switch, trigger, crank) centred at <paramref name="hz"/>.</summary>
    public static float[] Click(double hz = 1800, double len = .05, float gain = .5f)
    {
        var mono = Variant($"click{hz:0}", 2, seed =>
        {
            var tick = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(Math.Max(.03, len)), seed + 200), Biquad.BandPass(hz, 4)), 140);
            var ring = Dsp.Decay(Dsp.Sweep(Math.Max(.03, len), hz * .55, hz * .5, .01, 70), 0);
            return Dsp.Finish(Dsp.Add(tick, ring, .35f), 1f, 3);
        });
        return Dsp.Gain(Dsp.Place(mono, 0, .08f, .6, .2), gain);
    }

    /// <summary>Magazine out, slide, magazine in with a solid clack.</summary>
    public static float[] Reload() => Variant("reload", 2, seed =>
    {
        float[] Metal(double hz, double decay, int s) =>
            Dsp.Add(Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.08), s), Biquad.BandPass(hz, 5)), decay), Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.02), s + 1), Biquad.HighPass(4000)), 300), .5f);
        var slide = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.12), seed + 301), Biquad.BandPass(2600, 1.2)), 25);
        var m = Dsp.Add(Metal(1500, 90, seed + 310), slide, .35f, .1);
        m = Dsp.Add(m, Metal(900, 60, seed + 320), 1.2f, .3);
        m = Dsp.Add(m, Dsp.Sweep(.08, 220, 140, .01, 50), .5f, .3);
        return Dsp.Place(Dsp.Finish(m, .7f), 0, .1f, .6, .25);
    });

    /// <summary>Explosion: sub drop, rumbling low noise with a closing filter, sparse debris crackle, big room.</summary>
    public static float[] Explosion(float pan)
    {
        var mono = Variant("boom", 2, seed =>
        {
            int n = Dsp.N(2.2);
            var noise = Dsp.Noise(n, seed + 400); var lp = Biquad.LowPass(4000); var rumble = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                if (i % 64 == 0) lp.SetLowPass(200 + 3800 * Math.Exp(-t * 3));
                rumble[i] = lp.Process(noise[i]) * (float)(Math.Min(1, t / .004) * Math.Exp(-t * 1.6));
            }
            var sub = Dsp.Sweep(2.2, 70, 28, .3, 1.8);
            var r = new Random(seed); var debris = new float[n];
            for (int k = 0; k < 60; k++)
            {
                double at = .05 + r.NextDouble() * 1.2;
                var c = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.03), seed * 97 + k), Biquad.BandPass(1500 + r.NextDouble() * 3000, 3)), 150);
                debris = Dsp.Add(debris, c, (float)(.25 * Math.Exp(-at * 1.5)), at);
            }
            return Dsp.Finish(Dsp.Add(Dsp.Add(Dsp.Gain(rumble, 2.2f), sub, 1.3f), debris, 1), .85f, 40);
        });
        return Dsp.Place(mono, pan * .6f, .3f, 1.4, 1.2);
    }

    public static float[] Heartbeat() => Variant("heart", 1, _ =>
    {
        var lub = Dsp.Filter(Dsp.Sweep(.25, 70, 45, .05, 18), Biquad.LowPass(200));
        var dub = Dsp.Filter(Dsp.Sweep(.25, 60, 40, .05, 22), Biquad.LowPass(180));
        return Dsp.Place(Dsp.Finish(Dsp.Add(lub, dub, .75f, .24), .8f, 20), 0, 0);
    });

    /// <summary>Rifle-butt hit: a short swish and a dull thud.</summary>
    public static float[] Melee() => Variant("melee", 2, seed =>
    {
        var swish = new float[Dsp.N(.18)]; var noise = Dsp.Noise(swish.Length, seed + 500); var bp = Biquad.BandPass(900, 1.2);
        for (int i = 0; i < swish.Length; i++) swish[i] = bp.Process(noise[i]) * (float)Math.Sin(Math.PI * i / swish.Length) * .6f;
        var thud = Dsp.Filter(Dsp.Sweep(.25, 120, 60, .02, 22), Biquad.LowPass(500));
        return Dsp.Place(Dsp.Finish(Dsp.Add(swish, thud, 1.2f, .12), .85f), 0, .1f, .6, .3);
    });

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
