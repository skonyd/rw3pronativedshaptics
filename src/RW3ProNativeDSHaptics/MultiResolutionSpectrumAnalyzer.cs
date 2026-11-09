using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Lower-latency spectrum for Hybrid/Multiband: components at or above <see cref="CrossoverHz"/> come from
/// a 2048-sample window (42.7 ms history); the deep bass below it from the 4096-sample window (85.3 ms),
/// where the short window alone failed (55/240 audit cases, all 30–40 Hz, MULTIBAND_RESULTS_20261108).
/// A short-window peak that is really a mis-estimated deep tone (a long-window component below the
/// crossover within <see cref="DuplicateHz"/>) is dropped. Once 42.7 ms of audio exist the high
/// components are available; the bass joins when the long window is full.
/// Same contract as <see cref="HapticSpectrumAnalyzer"/>: ≤ 3 peaks, 30–400 Hz, low-to-high order.
/// </summary>
public sealed class MultiResolutionSpectrumAnalyzer
{
    public const int LongSamples = 4096, ShortSamples = 2048;
    public const float CrossoverHz = 60f;
    /// <summary>The long window keeps components up to here: an overlap, so a tone right at the crossover
    /// (long says 60.0, short says 59.x) is not lost between the two (audit 2026-11-09: 15 cases at 60 Hz).</summary>
    public const float LongBandTopHz = 70f;
    /// <summary>A long-window bass peak this close to a short-window peak means the short one is the same tone, mis-estimated.</summary>
    public const float DuplicateHz = 35f;   // the short window was at most 30 Hz off (30–40 Hz tones)
    /// <summary>Minimum spacing between merged peaks (3 bins of the short window ≈ 70 Hz is enforced inside it; across windows one short bin).</summary>
    public const float MergeSeparationHz = 48000f / ShortSamples;

    private readonly HapticSpectrumAnalyzer _long = new(LongSamples), _short = new(ShortSamples);

    /// <param name="window">The latest <see cref="LongSamples"/> samples, oldest first.</param>
    /// <param name="validSamples">How many of the newest samples are real audio (history may still be filling).</param>
    public HapticSpectrum Analyze(ReadOnlySpan<float> window, int validSamples = LongSamples)
    {
        if (window.Length != LongSamples) throw new ArgumentException("The latest 4096 original samples are required.", nameof(window));
        if (validSamples < ShortSamples) return default;
        var s = _short.Analyze(window[(LongSamples - ShortSamples)..]);
        var l = validSamples >= LongSamples ? _long.Analyze(window) : default;

        Span<HapticSpectralComponent> pool = stackalloc HapticSpectralComponent[6];
        int n = 0;
        for (int i = 0; i < l.Count; i++)
            if (l[i].FrequencyHz < LongBandTopHz) pool[n++] = l[i];
        for (int i = 0; i < s.Count; i++)
        {
            var c = s[i];
            if (c.FrequencyHz < CrossoverHz) continue;
            bool duplicate = false;
            for (int j = 0; j < l.Count; j++)
                if (l[j].FrequencyHz < LongBandTopHz && Math.Abs(l[j].FrequencyHz - c.FrequencyHz) <= DuplicateHz) duplicate = true;
            if (!duplicate) pool[n++] = c;
        }
        return Select(pool[..n]);
    }

    /// <summary>Strongest first, spaced, relative floor like the single-window analyzer (power .01 = amplitude .1), then low-to-high.</summary>
    private static HapticSpectrum Select(Span<HapticSpectralComponent> pool)
    {
        for (int i = 0; i < pool.Length; i++) for (int j = i + 1; j < pool.Length; j++)
            if (pool[j].Amplitude > pool[i].Amplitude) (pool[i], pool[j]) = (pool[j], pool[i]);
        Span<HapticSpectralComponent> pick = stackalloc HapticSpectralComponent[3];
        int count = 0;
        float largest = pool.Length > 0 ? pool[0].Amplitude : 0;
        foreach (var c in pool)
        {
            if (count == 3) break;
            if (c.Amplitude < largest * 0.1f) break;
            bool close = false;
            for (int k = 0; k < count; k++) if (Math.Abs(pick[k].FrequencyHz - c.FrequencyHz) < MergeSeparationHz) close = true;
            if (!close) pick[count++] = c;
        }
        for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++)
            if (pick[j].FrequencyHz < pick[i].FrequencyHz) (pick[i], pick[j]) = (pick[j], pick[i]);
        return new(count > 0 ? pick[0] : default, count > 1 ? pick[1] : default, count > 2 ? pick[2] : default, count);
    }
}
