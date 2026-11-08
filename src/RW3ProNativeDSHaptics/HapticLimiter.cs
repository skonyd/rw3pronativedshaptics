using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Output gain stage for the Sensa command values (envelope points, transient strengths).
///
/// At gain ≤ 1 this is a plain clamp: nothing changes for unboosted output or test pulses.
/// Above 1 it is a soft knee instead of a hard ceiling: values up to <see cref="Knee"/> pass
/// unchanged, larger ones approach 1 exponentially (slope 1 at the knee, never reaching 1).
/// 2026-11-08 scenario audit: at 140 % output gain, level-0.9 sources pinned 242 of 243 envelope
/// points at exactly 1.0, so every strong moment felt the same. With the knee, at 300 %:
/// 0.33 → 0.89, 0.5 → 0.98, 1.0 → ≈1.0 — boosted weak detail, strong moments still ordered.
/// </summary>
public static class HapticLimiter
{
    public const double Knee = 0.7;

    public static double Apply(double value, double gain)
    {
        double v = value * gain;
        if (!double.IsFinite(v) || v <= 0) return 0;
        if (gain <= 1.0 + 1e-9) return Math.Min(v, 1);
        if (v <= Knee) return v;
        return Knee + (1 - Knee) * (1 - Math.Exp(-(v - Knee) / (1 - Knee)));
    }
}
