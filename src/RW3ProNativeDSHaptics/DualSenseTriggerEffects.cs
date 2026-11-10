using System;

namespace RW3ProNativeDSHaptics;

/// <summary>One adaptive-trigger effect as a game sends it to a DualSense (11 bytes: mode + 10 parameters).</summary>
public readonly record struct TriggerEffect(byte Mode, byte P0, byte P1, byte P2, byte P3, byte P4, byte P5, byte P6, byte P7, byte P8, byte P9)
{
    public static TriggerEffect From(ReadOnlySpan<byte> b) => b.Length < 11 ? default
        : new(b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10]);
    public bool IsOff => Mode is 0x00 or 0x05;
}

/// <summary>
/// DualSense USB output report 0x02 as received from a game (HIDMaestro passes the bytes after the
/// report ID). Layout (public community documentation of the DualSense, e.g. the Linux hid-playstation
/// driver and DSX/DualSenseX notes): data[0..1] valid flags, data[2] right (light) rumble,
/// data[3] left (heavy) rumble, data[10..20] right-trigger effect, data[21..31] left-trigger effect.
/// </summary>
public readonly record struct DualSenseOutputReport(bool RumbleValid, byte RumbleLeft, byte RumbleRight,
    bool RightTriggerValid, TriggerEffect RightTrigger, bool LeftTriggerValid, TriggerEffect LeftTrigger)
{
    public const byte ReportId = 0x02;

    public static bool TryParse(ReadOnlySpan<byte> data, out DualSenseOutputReport report)
    {
        report = default;
        if (data.Length < 32) return false;
        byte f0 = data[0];
        report = new DualSenseOutputReport(
            // HAPTICS_SELECT (bit 1) alone does not validate the compatible-rumble bytes.
            RumbleValid: (f0 & 0x01) != 0 || (data.Length > 38 && (data[38] & 0x04) != 0), RumbleLeft: data[3], RumbleRight: data[2],
            RightTriggerValid: (f0 & 0x04) != 0, RightTrigger: TriggerEffect.From(data.Slice(10, 11)),
            LeftTriggerValid: (f0 & 0x08) != 0, LeftTrigger: TriggerEffect.From(data.Slice(21, 11)));
        return true;
    }
}

/// <summary>
/// Turns a DualSense adaptive-trigger effect into vibration for a controller that has no adaptive
/// triggers (Wolverine: rumble motors in PC mode, impulse triggers in Xbox mode). Resistance cannot
/// be reproduced; it is suggested by a short click where the resistance starts / the "trigger
/// breaks", scaled by the effect's force. Vibration effects vibrate while the trigger is in their range.
/// Called every update with the current trigger pull (0..1) and elapsed seconds.
/// </summary>
public sealed class TriggerEffectInterpreter
{
    public const double DefaultClickSeconds = 0.045;
    /// <summary>Click length. 45 ms suits the body motors; the impulse-trigger motors need longer to be felt.</summary>
    public double ClickSeconds { get; set; } = DefaultClickSeconds;
    /// <summary>Continuous "tension" while the trigger is inside a resistance zone (0 = clicks only, the
    /// body-motor default). For the impulse-trigger motors the resistance itself becomes a light vibration,
    /// so holding a drawn bow or a half-pulled weapon trigger is felt in the trigger, not only its edges.</summary>
    public double TensionScale { get; set; }
    /// <summary>Parts of the last <see cref="Update"/>: click level (while a click lasts), raw tension of the
    /// resistance zone (before <see cref="TensionScale"/>), and the vibration-effect level. Used by the palm feel.</summary>
    public double LastClick { get; private set; }
    public double LastTension { get; private set; }
    public double LastVibration { get; private set; }
    private TriggerEffect _effect;
    private double _lastPull, _clickLeft, _clickStrength, _phase, _amplitudePhase;
    public byte Mode => _effect.Mode;
    public bool Supported => _effect.Mode is 0 or 5 or 1 or 2 or 6 or 0x11 or 0x12 or 0x21 or 0x22 or 0x23 or 0x25 or 0x26 or 0x27;
    public void Reset() { _effect = default; LastClick = LastTension = LastVibration = 0; _lastPull = _clickLeft = _clickStrength = _phase = _amplitudePhase = 0; }

    public void SetEffect(in TriggerEffect effect)
    {
        if (effect != _effect) { _effect = effect; _clickLeft = _clickStrength = _phase = _amplitudePhase = 0; }
    }

    /// <summary>Motor intensity 0..1 for this update.</summary>
    public double Update(double pull, double dt)
    {
        pull = Math.Clamp(double.IsFinite(pull) ? pull : 0, 0, 1);
        dt = Math.Clamp(double.IsFinite(dt) ? dt : 0, 0, .1);
        var e = _effect;
        double steady = 0, tension = 0;
        void Click(double strength) { if (strength > 0) { _clickStrength = Math.Max(_clickStrength * (_clickLeft > 0 ? 1 : 0), strength); _clickLeft = ClickSeconds; } }
        bool Crossed(double at) => _lastPull < at && pull >= at;
        switch (e.Mode)
        {
            case 0x01: // continuous resistance: start position, force
            case 0x11: // limited simple feedback, same wire fields
                if (pull > 0 && Crossed(e.P0 / 255.0)) Click(e.P1 / 255.0);
                if (pull > 0 && pull >= e.P0 / 255.0) tension = e.P1 / 255.0;
                break;
            case 0x02: // section resistance: start, end, force
            case 0x12: // limited simple weapon
                if (Crossed(e.P0 / 255.0)) Click(e.P2 / 255.0);
                if (Crossed(e.P1 / 255.0)) Click(e.P2 / 255.0 * 0.6);
                if (pull > 0 && pull >= e.P0 / 255.0 && pull <= e.P1 / 255.0) tension = e.P2 / 255.0;
                break;
            case 0x06: // simple vibration: frequency, amplitude, start position
                if (pull > 0 && pull >= e.P2 / 255.0) steady = Vibrate(e.P1 / 255.0, e.P0, dt);
                break;
            case 0x21: // feedback: 10-zone mask (P0,P1) + 3-bit strengths
            {
                int zone = Math.Min(9, (int)(pull * 10)), prev = Math.Min(9, (int)(_lastPull * 10));
                int mask = e.P0 | (e.P1 << 8);
                bool inside = (mask & (1 << zone)) != 0 && pull > 0.02, wasInside = (mask & (1 << prev)) != 0 && _lastPull > 0.02;
                // A DualSense feedback zone is steady resistance, not a ratchet: click once where the resistance
                // starts (or gets stronger), then hold the tension. A click at every 10 % step made L2 feel fastest
                // while moving and weaker when held (user report 2026-11-09).
                if (inside && zone > prev && (!wasInside || ZoneStrength(e, zone) > ZoneStrength(e, prev))) Click(ZoneStrength(e, zone) / 8.0);
                if (inside) tension = ZoneStrength(e, zone) / 8.0;
                break;
            }
            case 0x25: // weapon: start zone mask (P0,P1 low/high), end zone, strength
            {
                int mask = e.P0 | (e.P1 << 8);
                int start = LowestZone(mask), end = HighestZone(mask);
                double strength = Math.Clamp((e.P2 & 0x07) + 1, 1, 8) / 8.0;
                if (start >= 0 && Crossed(start / 10.0)) Click(strength * .3);
                if (end > start && Crossed(end / 10.0)) Click(strength);
                if (start >= 0 && end > start && pull > 0 && pull >= start / 10.0 && pull < end / 10.0) tension = strength;
                break;
            }
            case 0x22: // bow: two zone endpoints, packed resistance and snap-back strengths
            {
                int mask = e.P0 | (e.P1 << 8), start = LowestZone(mask), end = HighestZone(mask);
                double strength = ((e.P2 & 7) + 1) / 8.0, snap = (((e.P2 >> 3) & 7) + 1) / 8.0;
                if (start >= 0 && end > start)
                {
                    if (Crossed(start / 10.0)) Click(strength * .3);
                    if (Crossed(end / 10.0) || (_lastPull >= end / 10.0 && pull < end / 10.0)) Click(snap);
                    if (pull > 0 && pull >= start / 10.0 && pull < end / 10.0) tension = strength;
                }
                break;
            }
            case 0x26: // vibration: 10-zone mask + 3-bit amplitudes, frequency
            {
                int zone = Math.Min(9, (int)(pull * 10));
                int mask = e.P0 | (e.P1 << 8);
                if (pull > .02 && (mask & (1 << zone)) != 0) steady = Vibrate(ZoneStrength(e, zone) / 8.0, e.P8, dt);
                break;
            }
            case 0x23: // galloping: two zone endpoints, two foot positions in an eight-part cycle
            {
                int mask = e.P0 | (e.P1 << 8), start = LowestZone(mask), end = HighestZone(mask);
                int first = (e.P2 >> 3) & 7, second = e.P2 & 7;
                if (start >= 0 && end > start && first < second && e.P3 > 0 && pull > .02 && pull >= start / 10.0 && pull <= end / 10.0)
                {
                    _phase = (_phase + e.P3 * dt) % 1;
                    // A two-beat impulse envelope is an approximation of the authored timing, not force replay.
                    steady = e.P3 > 30 ? .25 :
                        (_phase >= first / 8.0 && _phase < (first + 1) / 8.0) ||
                        (_phase >= second / 8.0 && _phase < (second + 1) / 8.0) ? 1 : 0;
                }
                break;
            }
            case 0x27: // machine: endpoints + two amplitudes + carrier frequency + period in 0.1s units
            {
                int mask = e.P0 | (e.P1 << 8), start = LowestZone(mask), end = HighestZone(mask);
                if (start >= 0 && end > start && pull > .02 && pull >= start / 10.0 && pull <= end / 10.0)
                {
                    if (e.P4 > 0) _amplitudePhase = (_amplitudePhase + dt / (e.P4 * .1)) % 1;
                    double a = (e.P2 & 7) / 7.0, b = ((e.P2 >> 3) & 7) / 7.0;
                    steady = Vibrate(_amplitudePhase < .5 ? a : b, e.P3, dt);
                }
                break;
            }
        }
        _lastPull = pull;
        LastTension = tension; LastVibration = steady;
        steady = Math.Max(steady, tension * TensionScale);
        double click = 0;
        if (_clickLeft > 0) { click = _clickStrength; _clickLeft -= dt; }
        LastClick = click;
        return Math.Clamp(Math.Max(steady, click), 0, 1);
    }

    // An impulse motor is amplitude-controlled, not a DualSense force actuator. Low-rate cycling is
    // represented as a vibration envelope. Above 30 Hz retain intensity, never alias an unknown carrier.
    private double Vibrate(double amplitude, byte frequency, double dt)
    {
        if (frequency == 0) return 0;
        if (frequency > 30) return amplitude;
        _phase = (_phase + frequency * dt) % 1;
        return _phase < .5 ? amplitude : 0;
    }

    /// <summary>Enabled zone strength is encoded as 0..7 → 1..8; the mask, not zero bits, disables it.</summary>
    private static int ZoneStrength(in TriggerEffect e, int zone)
    {
        ulong packed = e.P2 | ((ulong)e.P3 << 8) | ((ulong)e.P4 << 16) | ((ulong)e.P5 << 24);
        int v = (int)((packed >> (3 * zone)) & 0x7);
        return v + 1;
    }

    private static int LowestZone(int mask) { for (int i = 0; i < 10; i++) if ((mask & (1 << i)) != 0) return i; return -1; }
    private static int HighestZone(int mask) { for (int i = 9; i >= 0; i--) if ((mask & (1 << i)) != 0) return i; return -1; }
}
