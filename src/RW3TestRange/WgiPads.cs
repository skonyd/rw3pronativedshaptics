using Windows.Gaming.Input;

namespace RW3TestRange;

/// <summary>
/// Windows.Gaming.Input gamepads: the rumble path PowerA Controller Companion and many games use, with the two trigger
/// (impulse) motors. Unlike GameInput it passed trigger rumble to RW3's virtual Series pad (2026-11-22, E-20261122-17),
/// and it reaches a Wolverine on the wireless adapter. The list is read live: WGI adds pads a moment after start.
/// </summary>
public sealed class WgiPads
{
    public int Count { get { try { return Gamepad.Gamepads.Count; } catch { return 0; } } }

    /// <summary>"045E:0B13, 1532:0A3F": the pads WGI sees now.</summary>
    public string Names()
    {
        try
        {
            return string.Join(", ", Gamepad.Gamepads.Select(g =>
            {
                var raw = RawGameController.FromGameController(g);
                return raw == null ? "?" : $"{raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4}";
            }));
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>All four motors (0..1) on every WGI gamepad (or all but the Razer pad); returns how many took the write.</summary>
    public int Rumble(float low, float high, float lt, float rt, bool skipRazer = false)
    {
        int n = 0;
        try
        {
            foreach (var g in Gamepad.Gamepads)
            {
                try
                {
                    if (skipRazer && RawGameController.FromGameController(g)?.HardwareVendorId == 0x1532) continue;
                    g.Vibration = new GamepadVibration { LeftMotor = low, RightMotor = high, LeftTrigger = lt, RightTrigger = rt };
                    n++;
                }
                catch { }
            }
        }
        catch { }
        return n;
    }
}
