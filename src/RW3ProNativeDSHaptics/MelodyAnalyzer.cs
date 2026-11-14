using System;

namespace RW3ProNativeDSHaptics;

/// <summary>One analysis instant of one channel: the bass tone and the (octave-folded) melody tone.</summary>
public readonly record struct MelodyPoint(double BassHz, double BassAmp, double MelodyHz, double MelodyAmp);

/// <summary>
/// "Melodi" output: makes music recognisable on the Sensa palms, within what the stock firmware can play
/// (measured 2026-11-14, E-20261114-23): exact tones 30–400 Hz, a pitch per point (4 per 33 ms frame), 2 usable
/// bands, ~30 frames/s over the dongle. So each hand gets
/// <list type="bullet">
/// <item>band 1 = the bass: strongest tone 30–150 Hz, at its real pitch;</item>
/// <item>band 2 = the melody: the fundamental 110–1000 Hz (harmonic sum, so a voice's pitch wins over its loudest
/// overtone), folded by octaves into 150–400 Hz. The tune and its intervals stay; it sounds one or two octaves lower.</item>
/// </list>
/// Works on the RAW audio (the haptic extractor's 550 Hz band limit would remove most melodies). Input is
/// low-passed (2.4 kHz, 4th order) and decimated by 8 to 6 kHz. The bass uses an 85 ms Hann window (512 samples; 30 Hz
/// needs 2+ periods), the melody the newest 43 ms (256 samples), so a new note shows within ~20 ms instead of ~40 ms.
/// Both are zero-padded to 4096 (1.46 Hz bins) with parabolic refinement. Producer: <see cref="Append"/> (capture thread, under
/// the extractor lock). Consumer: <see cref="Analyze"/> on a snapshot (engine thread). No allocation after construction.
/// </summary>
public sealed class MelodyAnalyzer
{
    public const int InputRate = 48000, Decimation = 8, Rate = InputRate / Decimation;   // 6 kHz
    public const int RingSamples = 1024, WindowSamples = 512, MelodyWindowSamples = 256, FftSize = 4096;
    public const double BassLowHz = 30, BassHighHz = 150, MelodyLowHz = 110, MelodyHighHz = 1000;
    public const double FoldLowHz = 150, FoldHighHz = 400;
    /// <summary>Sinusoid amplitude below which a tone counts as absent (≈ -50 dBFS).</summary>
    public const double Floor = 0.003;
    /// <summary>The 4 points of a 33 ms frame are 11 ms apart: 66 samples at 6 kHz.</summary>
    public const int PointHopSamples = 66;

    private readonly ButterworthLowPass _lpL = new(2400, 4), _lpR = new(2400, 4);
    private readonly float[] _ringL = new float[RingSamples], _ringR = new float[RingSamples];
    private int _pos, _count, _phase;

    public int Count => _count;

    public void Reset()
    {
        _lpL.Reset(); _lpR.Reset();
        Array.Clear(_ringL); Array.Clear(_ringR);
        _pos = _count = _phase = 0;
    }

    /// <summary>One 48 kHz stereo sample.</summary>
    public void Append(float l, float r)
    {
        float a = _lpL.Process(l), b = _lpR.Process(r);
        if (++_phase < Decimation) return;
        _phase = 0;
        _ringL[_pos] = a; _ringR[_pos] = b;
        _pos = (_pos + 1) % RingSamples;
        _count = Math.Min(RingSamples, _count + 1);
    }

    /// <summary>Copies the ring, oldest first, into the snapshots (call under the producer's lock).</summary>
    public int Snapshot(float[] left, float[] right)
    {
        for (int i = 0; i < RingSamples; i++)
        {
            int p = (_pos + i) % RingSamples;
            left[i] = _ringL[p]; right[i] = _ringR[p];
        }
        return _count;
    }

    // ── analysis (consumer thread) ──
    private readonly double[] _re = new double[FftSize], _im = new double[FftSize], _mag = new double[FftSize / 2];
    private static readonly double[] Hann = MakeHann(WindowSamples), HannShort = MakeHann(MelodyWindowSamples);

    private static double[] MakeHann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    /// <summary>Hann window of <paramref name="w"/> over the end of <paramref name="window"/>, FFT, magnitudes.
    /// Returns the window sum (sinusoid amplitude = 2 · peak / sum).</summary>
    private double Spectrum(ReadOnlySpan<float> window, double[] w)
    {
        int n = w.Length, off = window.Length - n;
        double mean = 0, sum = 0;
        for (int i = 0; i < n; i++) mean += window[off + i];
        mean /= n;
        Array.Clear(_re); Array.Clear(_im);
        for (int i = 0; i < n; i++) { _re[i] = (window[off + i] - mean) * w[i]; sum += w[i]; }
        Fft(_re, _im);
        for (int k = 0; k < _mag.Length; k++) _mag[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
        return sum;
    }

    /// <summary>4 points (oldest first, 11 ms apart, the last ending at the newest sample) of one channel's snapshot.</summary>
    public void Analyze(ReadOnlySpan<float> snapshot, int valid, Span<MelodyPoint> points)
    {
        for (int k = 0; k < points.Length; k++)
        {
            int end = snapshot.Length - (points.Length - 1 - k) * PointHopSamples;
            int start = end - WindowSamples;
            points[k] = start < snapshot.Length - valid ? default : AnalyzeWindow(snapshot.Slice(start, WindowSamples));
        }
    }

    public MelodyPoint AnalyzeWindow(ReadOnlySpan<float> window)
    {
        const double bin = (double)Rate / FftSize;

        // bass: strongest local maximum 30–150 Hz, long window
        double sum = Spectrum(window, Hann);
        var (bassHz, bassAmp) = Peak((int)(BassLowHz / bin), (int)(BassHighHz / bin), bin, sum);

        // melody: newest 43 ms; harmonic sum picks the fundamental, then the local peak around it is refined
        sum = Spectrum(window, HannShort);
        int lo = (int)(MelodyLowHz / bin), hi = (int)(MelodyHighHz / bin);
        int best = 0; double score = 0;
        for (int k = lo; k <= hi; k++)
        {
            if (_mag[k] < _mag[k - 1] || _mag[k] < _mag[k + 1]) continue;
            double s = _mag[k] + .5 * Mag(2 * k) + .33 * Mag(3 * k);
            if (s > score) { score = s; best = k; }
        }
        double melHz = 0, melAmp = 0;
        if (best > 0)
        {
            (melHz, melAmp) = Refine(best, bin, sum);
            // the bass itself (or its octave) must not double as the melody
            if (bassAmp > 0 && Math.Abs(12 * Math.Log2(melHz / bassHz)) % 12 < .5 && melAmp < bassAmp * 1.2) melAmp = 0;
        }
        if (bassAmp < Floor) bassAmp = 0;
        if (melAmp < Floor) melAmp = 0;
        return new MelodyPoint(bassHz, bassAmp, melAmp > 0 ? Fold(melHz) : 0, melAmp);
    }

    /// <summary>Octave-folds a melody pitch into the band the palms play well (150–400 Hz).</summary>
    public static double Fold(double hz)
    {
        if (!(hz > 0) || !double.IsFinite(hz)) return 0;
        while (hz > FoldHighHz) hz /= 2;
        while (hz < FoldLowHz) hz *= 2;
        return hz;
    }

    private double Mag(int k) => k < _mag.Length ? _mag[k] : 0;

    private (double Hz, double Amp) Peak(int lo, int hi, double bin, double windowSum)
    {
        int best = 0; double m = 0;
        for (int k = Math.Max(1, lo); k <= hi; k++)
            if (_mag[k] >= _mag[k - 1] && _mag[k] >= _mag[k + 1] && _mag[k] > m) { m = _mag[k]; best = k; }
        return best == 0 ? (0, 0) : Refine(best, bin, windowSum);
    }

    private (double Hz, double Amp) Refine(int k, double bin, double windowSum)
    {
        double a = _mag[k - 1], b = _mag[k], c = _mag[k + 1];
        double den = a - 2 * b + c;
        double d = Math.Abs(den) > 1e-18 ? Math.Clamp(.5 * (a - c) / den, -.5, .5) : 0;
        double peak = b - .25 * (a - c) * d;
        return ((k + d) * bin, 2 * peak / windowSum);   // sinusoid amplitude
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            double wr0 = Math.Cos(-2 * Math.PI / size), wi0 = Math.Sin(-2 * Math.PI / size);
            for (int start = 0; start < n; start += size)
            {
                double wr = 1, wi = 0;
                for (int j = 0; j < size / 2; j++)
                {
                    int a = start + j, b = a + size / 2;
                    double tr = wr * re[b] - wi * im[b], ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    (wr, wi) = (wr * wr0 - wi * wi0, wr * wi0 + wi * wr0);
                }
            }
        }
    }

    /// <summary>Two Sensa bands for one hand from 4 analysis points: bass (band 1) and melody (band 2), each with a
    /// pitch per point. Strengths go through the same limiter and level as the standard path; their sum stays ≤ 1.
    /// A silent point keeps the band's previous pitch so the tone does not jump to 30 Hz between notes.</summary>
    public static int Bands(ReadOnlySpan<MelodyPoint> p, double level, Span<SensaBand> bands, ref double lastBassHz, ref double lastMelodyHz)
    {
        Span<double> ba = stackalloc double[4], bp = stackalloc double[4], ma = stackalloc double[4], mp = stackalloc double[4];
        bool any = false;
        for (int k = 0; k < 4; k++)
        {
            double b = HapticLimiter.Apply((float)p[k].BassAmp, level), m = HapticLimiter.Apply((float)p[k].MelodyAmp, level);
            double sum = b + m;
            if (sum > 1) { b /= sum; m /= sum; }
            ba[k] = b; ma[k] = m;
            if (p[k].BassAmp > 0) lastBassHz = p[k].BassHz;
            if (p[k].MelodyAmp > 0) lastMelodyHz = p[k].MelodyHz;
            bp[k] = SensaFrameEncoder.PitchOf(lastBassHz > 0 ? lastBassHz : 50);
            mp[k] = SensaFrameEncoder.PitchOf(lastMelodyHz > 0 ? lastMelodyHz : 200);
            any |= SensaFrameEncoder.Quantize6(b) > 0 || SensaFrameEncoder.Quantize6(m) > 0;
        }
        if (!any) return 0;
        bands[0] = new SensaBand(ba[0], ba[1], ba[2], ba[3], bp[0], bp[1], bp[2], bp[3]);
        bands[1] = new SensaBand(ma[0], ma[1], ma[2], ma[3], mp[0], mp[1], mp[2], mp[3]);
        return 2;
    }
}
