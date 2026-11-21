namespace RW3TestRange;

/// <summary>RBJ biquad filter (low / high / band pass, peak). Mono, per-instance state.</summary>
public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

    private Biquad Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0; return this;
    }

    private static (double cos, double alpha) W(double hz, double q)
    {
        double w = 2 * Math.PI * Math.Clamp(hz, 10, Synth.Rate * .45) / Synth.Rate;
        return (Math.Cos(w), Math.Sin(w) / (2 * q));
    }

    public static Biquad LowPass(double hz, double q = .707) { var b = new Biquad(); b.SetLowPass(hz, q); return b; }
    public static Biquad HighPass(double hz, double q = .707) { var (c, a) = W(hz, q); return new Biquad().Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a); }
    public static Biquad BandPass(double hz, double q = 1) { var (c, a) = W(hz, q); return new Biquad().Set(a, 0, -a, 1 + a, -2 * c, 1 - a); }

    /// <summary>Re-tunes a low-pass in place (for filter sweeps), keeping its state.</summary>
    public void SetLowPass(double hz, double q = .707) { var (c, a) = W(hz, q); Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a); }

    public float Process(float x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return (float)y;
    }
}

/// <summary>Small stereo room (Freeverb-style: 4 damped combs + 2 allpasses per side).</summary>
public sealed class Reverb
{
    private sealed class Comb
    {
        private readonly float[] _b; private int _i; private float _store;
        public Comb(int n) => _b = new float[n];
        public float Process(float x, float fb, float damp)
        {
            float y = _b[_i];
            _store = y * (1 - damp) + _store * damp;
            _b[_i] = x + _store * fb;
            if (++_i == _b.Length) _i = 0;
            return y;
        }
    }
    private sealed class AllPass
    {
        private readonly float[] _b; private int _i;
        public AllPass(int n) => _b = new float[n];
        public float Process(float x)
        {
            float b = _b[_i], y = -x + b;
            _b[_i] = x + b * .5f;
            if (++_i == _b.Length) _i = 0;
            return y;
        }
    }
    private readonly Comb[] _l, _r; private readonly AllPass[] _al, _ar;
    public float Feedback = .78f, Damp = .35f;

    public Reverb(double size = 1)
    {
        int[] c = { 1557, 1617, 1491, 1422 }; int[] a = { 556, 441 };
        _l = c.Select(n => new Comb((int)(n * size))).ToArray();
        _r = c.Select(n => new Comb((int)((n + 23) * size))).ToArray();
        _al = a.Select(n => new AllPass(n)).ToArray();
        _ar = a.Select(n => new AllPass(n + 23)).ToArray();
    }

    public (float L, float R) Process(float x)
    {
        x *= .25f;
        float l = 0, r = 0;
        foreach (var c in _l) l += c.Process(x, Feedback, Damp);
        foreach (var c in _r) r += c.Process(x, Feedback, Damp);
        foreach (var p in _al) l = p.Process(l);
        foreach (var p in _ar) r = p.Process(r);
        return (l, r);
    }
}

public static class Dsp
{
    public const int Rate = Synth.Rate;

    public static int N(double seconds) => (int)(seconds * Rate);

    public static float[] Noise(int n, int seed)
    {
        var r = new Random(seed); var b = new float[n];
        for (int i = 0; i < n; i++) b[i] = (float)(r.NextDouble() * 2 - 1);
        return b;
    }

    public static float[] Filter(float[] x, Biquad f) { var y = new float[x.Length]; for (int i = 0; i < x.Length; i++) y[i] = f.Process(x[i]); return y; }

    /// <summary>Multiplies by exp(-t·rate), starting at <paramref name="start"/> s (silence before).</summary>
    public static float[] Decay(float[] x, double rate, double start = 0, double attack = .0015)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / (double)Rate - start;
            if (t < 0) continue;
            double env = Math.Min(1, t / attack) * Math.Exp(-t * rate);
            y[i] = (float)(x[i] * env);
        }
        return y;
    }

    /// <summary>Sine whose pitch glides exponentially from <paramref name="f0"/> to <paramref name="f1"/> with time constant <paramref name="tau"/>.</summary>
    public static float[] Sweep(double seconds, double f0, double f1, double tau, double decay)
    {
        var y = new float[N(seconds)]; double ph = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double t = i / (double)Rate, f = f1 + (f0 - f1) * Math.Exp(-t / tau);
            ph += 2 * Math.PI * f / Rate;
            y[i] = (float)(Math.Sin(ph) * Math.Min(1, t / .001) * Math.Exp(-t * decay));
        }
        return y;
    }

    /// <summary>Karplus-Strong plucked string: a real-sounding bow / crossbow string.</summary>
    public static float[] String(double hz, double seconds, double brightness, int seed, double damping = .996)
    {
        var y = new float[N(seconds)];
        int period = Math.Max(2, (int)(Rate / hz));
        var line = Noise(period, seed);
        var lp = Biquad.LowPass(1500 + 6000 * brightness);
        for (int i = 0; i < period; i++) line[i] = lp.Process(line[i]);
        int k = 0; float prev = 0;
        for (int i = 0; i < y.Length; i++)
        {
            float cur = line[k];
            y[i] = cur;
            line[k] = (float)((cur + prev) * .5 * damping);
            prev = cur;
            if (++k == period) k = 0;
        }
        return y;
    }

    public static float[] Add(float[] a, float[] b, float gb = 1, double at = 0)
    {
        int off = N(at);
        var y = new float[Math.Max(a.Length, b.Length + off)];
        Array.Copy(a, y, a.Length);
        for (int i = 0; i < b.Length; i++) y[i + off] += b[i] * gb;
        return y;
    }

    public static float[] Gain(float[] a, float g) { for (int i = 0; i < a.Length; i++) a[i] *= g; return a; }

    /// <summary>Gentle saturation: unchanged below ±0.6, rounds off above (no hard clipping).</summary>
    public static float Soft(float x)
    {
        float a = MathF.Abs(x);
        if (a <= .6f) return x;
        return MathF.Sign(x) * (.6f + .4f * MathF.Tanh((a - .6f) / .4f));
    }

    /// <summary>Normalises a mono clip to <paramref name="peak"/>, saturates, and fades the last ms (no end clicks).</summary>
    public static float[] Finish(float[] x, float peak = .9f, double fadeMs = 8)
    {
        float max = 1e-6f; foreach (float v in x) max = Math.Max(max, MathF.Abs(v));
        int fade = N(fadeMs / 1000);
        for (int i = 0; i < x.Length; i++)
        {
            float v = Soft(x[i] / max * peak * 1.1f);
            if (i >= x.Length - fade) v *= (x.Length - i) / (float)fade;
            x[i] = v;
        }
        return x;
    }

    /// <summary>Mono clip → stereo: panned dry signal plus an unpanned room tail.</summary>
    public static float[] Place(float[] mono, float pan, float wet, double roomSize = 1, double tail = .6)
    {
        int n = mono.Length + (wet > 0 ? N(tail) : 0);
        var y = new float[n * 2];
        double a = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        float gl = (float)Math.Cos(a), gr = (float)Math.Sin(a);
        var rv = wet > 0 ? new Reverb(roomSize) : null;
        for (int i = 0; i < n; i++)
        {
            float x = i < mono.Length ? mono[i] : 0;
            float l = x * gl, r = x * gr;
            if (rv != null)
            {
                var (wl, wr) = rv.Process(x);
                float fade = i < mono.Length ? 1 : 1 - (i - mono.Length) / (float)(n - mono.Length);
                l += wl * wet * fade; r += wr * wet * fade;
            }
            y[2 * i] = l; y[2 * i + 1] = r;
        }
        return y;
    }
}
