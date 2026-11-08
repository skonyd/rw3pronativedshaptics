using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Streaming windowed-sinc sample-rate converter for interleaved stereo float (fixed ratio).
/// Replaces linear interpolation in the audio→haptics capture: measured 2026-11-08 on a 44.1 kHz
/// Bluetooth endpoint, linear 44.1→48 kHz conversion changed sharp onsets by up to 0.17 and made
/// 5 of 94 reference signals differ from Native (transient counts ±1–2, frequency spikes).
/// 32 taps, Kaiser β = 8, cutoff at the lower Nyquist; ≈ 16 input samples of look-ahead
/// (0.36 ms at 44.1 kHz). Allocation free after warm-up.
/// </summary>
public sealed class SincResampler
{
    public const int HalfTaps = 16;
    private const int TablePhases = 512;
    private const double Beta = 8.0;

    private readonly double _step;           // input samples per output sample
    private readonly float[] _table;          // [phase][tap] windowed sinc
    private float[] _hist = new float[4096];  // interleaved stereo input not yet consumed
    private int _histFrames = HalfTaps;       // primed with zeros: output sample 0 = input sample 0
    private double _pos = HalfTaps;           // next output position, in input frames relative to _hist[0]

    public int InputRate { get; }
    public int OutputRate { get; }

    public SincResampler(int inputRate, int outputRate)
    {
        InputRate = inputRate; OutputRate = outputRate;
        _step = inputRate / (double)outputRate;
        double cutoff = Math.Min(1.0, outputRate / (double)inputRate) * 0.97;   // a little below the lower Nyquist
        int taps = 2 * HalfTaps;
        _table = new float[(TablePhases + 1) * taps];
        double i0b = BesselI0(Beta);
        for (int ph = 0; ph <= TablePhases; ph++)
        {
            double frac = ph / (double)TablePhases;
            double sum = 0;
            for (int k = 0; k < taps; k++)
            {
                double x = k - (HalfTaps - 1) - frac;                    // distance from the output point
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double w = x / HalfTaps; w = Math.Abs(w) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - w * w)) / i0b;
                double v = cutoff * sinc * w;
                _table[ph * taps + k] = (float)v; sum += v;
            }
            for (int k = 0; k < taps; k++) _table[ph * taps + k] /= (float)sum;   // exact DC gain 1
        }
    }

    private static double BesselI0(double x)
    {
        double s = 1, t = 1, y = x * x / 4;
        for (int k = 1; k < 50; k++) { t *= y / (k * k); s += t; if (t < 1e-12 * s) break; }
        return s;
    }

    public void Reset() { Array.Clear(_hist, 0, HalfTaps * 2); _histFrames = HalfTaps; _pos = HalfTaps; }

    /// <summary>Converts <paramref name="input"/> (interleaved stereo) and writes into <paramref name="output"/>; returns floats written.</summary>
    public int Process(ReadOnlySpan<float> input, ref float[] output)
    {
        int inFrames = input.Length / 2;
        if (_hist.Length < (_histFrames + inFrames) * 2) Array.Resize(ref _hist, (_histFrames + inFrames) * 2 * 2);
        input.Slice(0, inFrames * 2).CopyTo(_hist.AsSpan(_histFrames * 2));
        _histFrames += inFrames;

        int maxOut = (int)((_histFrames - _pos) / _step) + 2;
        if (output.Length < maxOut * 2) output = new float[maxOut * 2];
        int o = 0, taps = 2 * HalfTaps;
        while (true)
        {
            int baseIdx = (int)Math.Floor(_pos);
            if (baseIdx + HalfTaps >= _histFrames) break;                  // need look-ahead
            double frac = _pos - baseIdx;
            int ph = (int)Math.Round(frac * TablePhases);
            int t0 = ph * taps, first = baseIdx - (HalfTaps - 1);
            float l = 0, r = 0;
            for (int k = 0; k < taps; k++)
            {
                int j = first + k; if (j < 0) continue;
                float c = _table[t0 + k];
                l += c * _hist[2 * j]; r += c * _hist[2 * j + 1];
            }
            output[o++] = l; output[o++] = r;
            _pos += _step;
        }
        // Drop consumed input, keep what the next outputs still need.
        int keepFrom = Math.Max(0, (int)Math.Floor(_pos) - HalfTaps);
        if (keepFrom > 0)
        {
            Array.Copy(_hist, keepFrom * 2, _hist, 0, (_histFrames - keepFrom) * 2);
            _histFrames -= keepFrom; _pos -= keepFrom;
        }
        return o;
    }
}
