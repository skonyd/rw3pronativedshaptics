using System;

namespace RW3ProNativeDSHaptics;

/// <summary>How the v2 engine turns features into Wolverine commands.</summary>
public enum SensaOutputMethod
{
    /// <summary>One frequency per hand (zero-crossing), fast 4-point envelope, transients. Default.</summary>
    Standard = 0,
    /// <summary>Experimental: up to three spectral components (multi-resolution FFT: 42.7 ms for >= 60 Hz, 85.3 ms below) as .haps melodies.</summary>
    Multiband = 1,
    /// <summary>Experimental: Standard for onsets/hits, Multiband only for steady mixed sounds.</summary>
    Hybrid = 2,
}

/// <summary>
/// Per-hand decision for <see cref="SensaOutputMethod.Hybrid"/>, plus the amplitude mapping.
///
/// 2026-11-08 microphone comparison (60 cases, same sources): Multiband put a mixed 80+190 Hz
/// scene at 80 and 190 Hz where Standard played its zero-crossing average (~120–135 Hz), but it
/// lost short hits almost entirely (−9.8 dB in all five repeats): its amplitude came from the
/// 85 ms spectrum, which dilutes a 20 ms hit. Hybrid therefore:
///   • always takes STRENGTH from the fast 1 ms-slot envelope (hits keep full strength);
///   • uses spectral components only to SPLIT that strength across frequencies, and only when
///     the sound is steady (no hit in the frame, envelope not jumping, active ≥ half the FFT
///     window so the history is mostly the current sound) and really mixed (≥ 2 components,
///     second strongest ≥ <see cref="SecondComponentRatio"/> of the strongest);
///   • enters Multiband after <see cref="EnterSteadyFrames"/> steady frames, leaves at once.
/// </summary>
public sealed class HybridOutputPolicy
{
    public const float ActiveThreshold = 0.02f;
    public const float SecondComponentRatio = 0.20f;
    public const float SteadyEnvelopeRatio = 2.0f;        // max/min of the 4 points within a frame
    public const int EnterSteadyFrames = 2;
    // Half the window the spectrum's >= 60 Hz components come from (multi-resolution short window): 21.3 ms
    // (was half of 4096 = 42.7 ms before 2026-11-09).
    public static readonly double SettleSeconds = MultiResolutionSpectrumAnalyzer.ShortSamples / 2.0 / 48000.0;

    private double _activeSince = -1;
    private int _steadyFrames;

    public bool Multi { get; private set; }

    public void Reset() { _activeSince = -1; _steadyFrames = 0; Multi = false; }

    public bool Decide(in SideFeatures f, in HapticSpectrum spectrum, bool haveSpectrum, double now)
    {
        if (!(f.Peak > ActiveThreshold)) { Reset(); return false; }
        if (_activeSince < 0) _activeSince = now;
        bool steady = f.TransientCount == 0 && IsSteadyEnvelope(f);
        _steadyFrames = steady ? _steadyFrames + 1 : 0;
        bool mixed = haveSpectrum && IsMixed(spectrum) && now - _activeSince >= SettleSeconds;
        Multi = mixed && steady && (Multi || _steadyFrames >= EnterSteadyFrames);
        return Multi;
    }

    public static bool IsSteadyEnvelope(in SideFeatures f)
    {
        float max = Math.Max(Math.Max(f.P0, f.P1), Math.Max(f.P2, f.P3));
        float min = Math.Min(Math.Min(f.P0, f.P1), Math.Min(f.P2, f.P3));
        return max <= Math.Max(min, 1e-4f) * SteadyEnvelopeRatio;
    }

    public static bool IsMixed(in HapticSpectrum s)
    {
        if (s.Count < 2) return false;
        float strongest = 0, second = 0;
        for (int i = 0; i < s.Count; i++)
        {
            float a = s[i].Amplitude;
            if (!float.IsFinite(a) || a < 0) return false;
            if (a >= strongest) { second = strongest; strongest = a; }
            else if (a > second) second = a;
        }
        // A small third peak must not veto two independently strong components.
        return strongest > 0 && second >= strongest * SecondComponentRatio;
    }

    /// <summary>Strongest component (for standard frames of a mixed sound).</summary>
    public static HapticSpectralComponent Dominant(in HapticSpectrum s)
    {
        var best = default(HapticSpectralComponent);
        for (int i = 0; i < s.Count; i++) if (s[i].Amplitude > best.Amplitude) best = s[i];
        return best;
    }

    /// <summary>
    /// Standard-frame pitch inside Hybrid. The zero-crossing estimate of a mixture is the average of
    /// its components (80+190 Hz reads ~130 Hz), a frequency the source does not contain. While a
    /// mixed sound starts (before Multiband engages) the standard frame plays its strongest real
    /// component instead. 2026-11-08 run b: those onset frames alone left the mixed scene at
    /// 4.4× background in the 120–135 Hz bands. Single tones and hits are not mixed: unchanged.
    /// </summary>
    public static SideFeatures StandardFrame(in SideFeatures f, in HapticSpectrum s, bool haveSpectrum)
    {
        if (!haveSpectrum || !IsMixed(s)) return f;
        var g = f;
        g.FrequencyHz = Dominant(s).FrequencyHz;
        return g;
    }

    /// <summary>Components keep their frequencies and relative shares; their sum follows the fast envelope peak.</summary>
    public static HapticSpectrum ScaleToEnvelope(in HapticSpectrum s, float envelopePeak)
    {
        float sum = s.AmplitudeSum;
        if (!(sum > 0) || !(envelopePeak > 0)) return default;
        float k = envelopePeak / sum;
        HapticSpectralComponent C(HapticSpectralComponent c) => c with { Amplitude = c.Amplitude * k };
        return new HapticSpectrum(s.Count > 0 ? C(s.A) : default, s.Count > 1 ? C(s.B) : default, s.Count > 2 ? C(s.C) : default, s.Count);
    }
}
