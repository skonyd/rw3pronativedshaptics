using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Soft ceiling for the Wolverine's big (heavy, left) body motor. Lying on a desk it rattles against the surface at
/// full power (22-30 ticks/s at the mic in Xbox mode, 2026-11-10, TestResults/body-motor-ticks-20261110); held in
/// the hand it does not tick at any level, so the ceiling is an option for a pad lying on a table, off by default.
/// Commands up to <see cref="Knee"/> pass unchanged; above it they approach <see cref="Ceiling"/> smoothly, so loudness order is kept. Applied at every place RW3 itself writes the body motors (XInput and
/// GameInput); a game's own rumble in Normal mode does not pass through RW3 and is untouched.
/// </summary>
public static class BodyMotorLimit
{
    public const double Knee = 0.6, Ceiling = 0.75;

    /// <summary>App-wide setting (Settings page), default off.</summary>
    public static volatile bool Enabled;

    /// <summary>Big motor command after the ceiling (0..1 in, 0..<see cref="Ceiling"/> out when enabled).</summary>
    public static double Big(double v) => Big(v, Enabled);

    public static double Big(double v, bool enabled)
    {
        v = double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        if (!enabled || v <= Knee) return v;
        double room = Ceiling - Knee;
        // Exponential approach: slope 1 at the knee, never above the ceiling.
        return Knee + room * (1 - Math.Exp(-(v - Knee) / room));
    }
}
