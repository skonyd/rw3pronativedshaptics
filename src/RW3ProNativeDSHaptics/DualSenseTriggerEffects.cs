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
            RumbleValid: (f0 & 0x03) != 0, RumbleLeft: data[3], RumbleRight: data[2],
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
    private TriggerEffect _effect;
    private double _lastPull, _clickLeft, _clickStrength;

    public void SetEffect(in TriggerEffect effect)
    {
        if (effect != _effect) { _effect = effect; _clickLeft = 0; }
    }

    /// <summary>Motor intensity 0..1 for this update.</summary>
    public double Update(double pull, double dt)
    {
        pull = Math.Clamp(double.IsFinite(pull) ? pull : 0, 0, 1);
        var e = _effect;
        double steady = 0, tension = 0;
        void Click(double strength) { if (strength > 0) { _clickStrength = Math.Max(_clickStrength * (_clickLeft > 0 ? 1 : 0), strength); _clickLeft = ClickSeconds; } }
        bool Crossed(double at) => _lastPull < at && pull >= at;
        switch (e.Mode)
        {
            case 0x01: // continuous resistance: start position, force
                if (Crossed(e.P0 / 255.0)) Click(Math.Max(0.25, e.P1 / 255.0));
                if (pull >= e.P0 / 255.0) tension = Math.Max(0.25, e.P1 / 255.0);
                break;
            case 0x02: // section resistance: start, end, force
                if (Crossed(e.P0 / 255.0)) Click(Math.Max(0.25, e.P2 / 255.0));
                if (Crossed(e.P1 / 255.0)) Click(Math.Max(0.2, e.P2 / 255.0) * 0.6);
                if (pull >= e.P0 / 255.0 && pull <= e.P1 / 255.0) tension = Math.Max(0.25, e.P2 / 255.0);
                break;
            case 0x06: // simple vibration: frequency, amplitude, start position
                if (pull >= e.P2 / 255.0) steady = e.P1 / 255.0;
                break;
            case 0x21: // feedback: 10-zone mask (P0,P1) + 3-bit strengths
            case 0x27:
            {
                int zone = Math.Min(9, (int)(pull * 10)), prev = Math.Min(9, (int)(_lastPull * 10));
                int mask = e.P0 | (e.P1 << 8);
                if (zone > prev && (mask & (1 << zone)) != 0) Click(ZoneStrength(e, zone) / 8.0);
                if ((mask & (1 << zone)) != 0 && pull > 0.02) tension = ZoneStrength(e, zone) / 8.0;
                break;
            }
            case 0x25: // weapon: start zone mask (P0,P1 low/high), end zone, strength
            {
                int mask = e.P0 | (e.P1 << 8);
                int start = LowestZone(mask), end = HighestZone(mask);
                double strength = Math.Clamp((e.P2 & 0x07) + 1, 1, 8) / 8.0;
                if (start >= 0 && Crossed(start / 10.0)) Click(0.3);
                if (end >= 0 && Crossed(Math.Min(1, (end + 1) / 10.0))) Click(strength);   // the "break"
                if (start >= 0 && pull >= start / 10.0 && pull < Math.Min(1, (end + 1) / 10.0)) tension = strength;
                break;
            }
            case 0x26: // vibration: 10-zone mask + 3-bit amplitudes, frequency
            {
                int zone = Math.Min(9, (int)(pull * 10));
                int mask = e.P0 | (e.P1 << 8);
                if ((mask & (1 << zone)) != 0) steady = ZoneStrength(e, zone) / 8.0;
                break;
            }
        }
        _lastPull = pull;
        steady = Math.Max(steady, tension * TensionScale);
        double click = 0;
        if (_clickLeft > 0) { click = _clickStrength; _clickLeft -= dt; }
        return Math.Clamp(Math.Max(steady, click), 0, 1);
    }

    /// <summary>3-bit strength of a zone, packed from P2 onwards (zone n at bit 3n, 0 = off, 1..7).</summary>
    private static int ZoneStrength(in TriggerEffect e, int zone)
    {
        ulong packed = e.P2 | ((ulong)e.P3 << 8) | ((ulong)e.P4 << 16) | ((ulong)e.P5 << 24);
        int v = (int)((packed >> (3 * zone)) & 0x7);
        return v == 0 ? 0 : v + 1;
    }

    private static int LowestZone(int mask) { for (int i = 0; i < 10; i++) if ((mask & (1 << i)) != 0) return i; return -1; }
    private static int HighestZone(int mask) { for (int i = 9; i >= 0; i--) if ((mask & (1 << i)) != 0) return i; return -1; }
}
