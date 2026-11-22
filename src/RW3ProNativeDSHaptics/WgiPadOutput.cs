using System;
using Windows.Gaming.Input;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Standard Xbox rumble (two body motors + two impulse triggers) on the Wolverine through Windows.Gaming.Input: the path
/// games and PowerA Controller Companion use. It reaches the pad through the wireless adapter too, where RW3's Razer
/// Sensa frames (palms, Sensa trigger drive) did not (2026-11-22, E-20261122-18). Like every standard path it may be
/// held back while another controller app is in front.
/// </summary>
public sealed class WgiPadOutput
{
    private Gamepad? _pad;
    private long _nextLook;
    public long Writes { get; private set; }
    public string State { get; private set; } = "-";

    /// <summary>All four motors at once (0..1). False when the Wolverine is not (yet) visible to Windows.Gaming.Input.</summary>
    public bool Set(double bodyLeft, double bodyRight, double triggerLeft, double triggerRight)
    {
        var pad = Find();
        if (pad == null) return false;
        try
        {
            pad.Vibration = new GamepadVibration
            {
                LeftMotor = Clamp(bodyLeft), RightMotor = Clamp(bodyRight),
                LeftTrigger = Clamp(triggerLeft), RightTrigger = Clamp(triggerRight)
            };
            Writes++;
            return true;
        }
        catch (Exception ex) { State = ex.Message; _pad = null; return false; }
    }

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    /// <summary>The Razer pad among WGI's gamepads (never the 045E virtual pad); looked up at most every 2 s.</summary>
    private Gamepad? Find()
    {
        if (_pad != null) return _pad;
        long now = Environment.TickCount64;
        if (now < _nextLook) return null;
        _nextLook = now + 2000;
        try
        {
            foreach (var g in Gamepad.Gamepads)
            {
                var raw = RawGameController.FromGameController(g);
                if (raw?.HardwareVendorId != 0x1532) continue;
                _pad = g;
                State = $"Wolverine 1532:{raw.HardwareProductId:X4}";
                return g;
            }
            State = "Wolverine görünmüyor";
        }
        catch (Exception ex) { State = ex.Message; }
        return null;
    }
}
