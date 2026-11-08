using System;

namespace RW3ProNativeDSHaptics;

public readonly record struct HapticSpectralComponent(float FrequencyHz, float Amplitude, float PhaseRadians);
public readonly record struct HapticSpectrum(HapticSpectralComponent A, HapticSpectralComponent B,
    HapticSpectralComponent C, int Count)
{
    public HapticSpectralComponent this[int i] => i switch { 0 => A, 1 => B, 2 => C, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
    public float AmplitudeSum => (Count > 0 ? A.Amplitude : 0) + (Count > 1 ? B.Amplitude : 0) + (Count > 2 ? C.Amplitude : 0);
}

/// <summary>
/// Experimental, bounded Hann/FFT analysis of ORIGINAL samples. Up to three resolved peaks,
/// not waveform recovery. 4096 samples =85.33ms history; selected bins are >=3 bins apart.
/// Phase is relative to the start of this window and is NOT a motor phase command.
/// Construct once, call on one consumer thread; never run FFT in a WASAPI callback.
/// </summary>
public sealed class HapticSpectrumAnalyzer
{
    public const int WindowSamples = 4096;
    public const double BinHz = 48000d / WindowSamples;
    public const double MinimumSeparationHz = 3 * BinHz;
    private readonly int _size;
    private readonly double[] _real, _imag, _window, _power;
    private readonly double _windowSum;

    public HapticSpectrumAnalyzer(int windowSamples = WindowSamples)
    {
        if (windowSamples is not (2048 or 4096)) throw new ArgumentOutOfRangeException(nameof(windowSamples));
        _size = windowSamples;
        _real = new double[_size]; _imag = new double[_size]; _window = new double[_size]; _power = new double[_size / 2];
        for (int i = 0; i < _size; i++) { _window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / _size); _windowSum += _window[i]; }
    }

    public HapticSpectrum Analyze(ReadOnlySpan<float> mono)
    {
        if (mono.Length != _size) throw new ArgumentException("A complete configured original 48k window is required.", nameof(mono));
        double mean = 0;
        for (int i = 0; i < mono.Length; i++) mean += float.IsFinite(mono[i]) ? mono[i] : 0;
        mean /= mono.Length;
        for (int i = 0; i < mono.Length; i++) { _real[i] = ((float.IsFinite(mono[i]) ? mono[i] : 0) - mean) * _window[i]; _imag[i] = 0; }
        for (int i = 1, j = 0; i < _size; i++) {
            int bit = _size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (_real[i], _real[j]) = (_real[j], _real[i]); (_imag[i], _imag[j]) = (_imag[j], _imag[i]); }
        }
        for (int size = 2; size <= _size; size <<= 1) {
            double wr0 = Math.Cos(-2 * Math.PI / size), wi0 = Math.Sin(-2 * Math.PI / size);
            for (int start = 0; start < _size; start += size) {
                double wr = 1, wi = 0;
                for (int j = 0; j < size / 2; j++) {
                    int a = start + j, b = a + size / 2;
                    double tr = wr * _real[b] - wi * _imag[b], ti = wr * _imag[b] + wi * _real[b];
                    _real[b] = _real[a] - tr; _imag[b] = _imag[a] - ti; _real[a] += tr; _imag[a] += ti;
                    (wr, wi) = (wr * wr0 - wi * wi0, wr * wi0 + wi * wr0);
                }
            }
        }
        for (int k = 0; k < _power.Length; k++) _power[k] = _real[k] * _real[k] + _imag[k] * _imag[k];
        Span<HapticSpectralComponent> peaks = stackalloc HapticSpectralComponent[3];
        Span<int> bins = stackalloc int[3]; int count = 0;
        double largest = 0;
        double binHz = 48000d / _size;
        for (int n = 0; n < 3; n++) {
            int best = 0; double power = 0;
            for (int k = Math.Max(1, (int)Math.Floor(30 / binHz)); k <= (int)Math.Ceiling(400 / binHz); k++) {
                if (_power[k] < _power[k - 1] || _power[k] < _power[k + 1]) continue;
                bool close = false;
                for (int p = 0; p < count; p++) if (Math.Abs(k - bins[p]) <= 2) close = true;
                if (!close && _power[k] > power) { best = k; power = _power[k]; }
            }
            if (best == 0 || power < 1e-12 || n > 0 && power < largest * .01) break;
            if (n == 0) largest = power;
            double a = Math.Log(Math.Max(_power[best - 1], 1e-30)), b = Math.Log(power), c = Math.Log(Math.Max(_power[best + 1], 1e-30));
            double delta = Math.Clamp(.5 * (a - c) / (a - 2 * b + c), -.5, .5);
            double hz = (best + delta) * binHz;
            if (!double.IsFinite(hz) || hz < 29 || hz > 401) { _power[best] = 0; n--; continue; }
            hz = Math.Clamp(hz, 30, 400); // <=1Hz edge-estimation allowance, output remains device-bounded.
            // Project original samples at the refined frequency for signed phase/amplitude.
            double stepR = Math.Cos(2 * Math.PI * hz / 48000), stepI = Math.Sin(2 * Math.PI * hz / 48000);
            double cr = 1, si = 0, cosine = 0, sine = 0;
            for (int i = 0; i < mono.Length; i++) {
                double sample = ((float.IsFinite(mono[i]) ? mono[i] : 0) - mean) * _window[i];
                cosine += sample * cr; sine += sample * si;
                (cr, si) = (cr * stepR - si * stepI, cr * stepI + si * stepR);
            }
            double amplitude = 2 * Math.Sqrt(cosine * cosine + sine * sine) / _windowSum;
            if (amplitude < .004) break;
            bins[count] = best;
            peaks[count++] = new((float)hz, (float)amplitude, (float)Math.Atan2(cosine, sine));
        }
        // Stable low-to-high melody order: equal-strength peaks must not swap bands every tick.
        for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++)
            if (peaks[j].FrequencyHz < peaks[i].FrequencyHz) (peaks[i], peaks[j]) = (peaks[j], peaks[i]);
        return new(count > 0 ? peaks[0] : default, count > 1 ? peaks[1] : default, count > 2 ? peaks[2] : default, count);
    }
}
