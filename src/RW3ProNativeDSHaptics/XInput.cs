using System;
using System.Runtime.InteropServices;

namespace RW3ProNativeDSHaptics;

/// <summary>The one XInput interop for the app (was copied into the bridge, the Xbox audio sink and
/// XInputHaptics). xinput1_4 only; state with the Guide bit through the stable ordinal 100 when present.</summary>
internal static class XInput
{
    public const uint Success = 0, NotConnected = 1167;

    [StructLayout(LayoutKind.Sequential)] public struct Gamepad { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)] public struct State { public uint dwPacketNumber; public Gamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] public struct Vibration { public ushort wLeftMotorSpeed, wRightMotorSpeed; }

    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint index, out State state);
    [DllImport("xinput1_4.dll")] public static extern uint XInputSetState(uint index, ref Vibration vibration);
    // Undocumented but long-stable ordinal 100: same state plus the Guide (Xbox) button bit 0x0400.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint XInputGetStateEx(uint index, out State state);
    private static bool _exMissing;

    /// <summary>State including the Guide button when the system offers it.</summary>
    public static uint GetState(uint index, out State state)
    {
        if (!_exMissing)
        {
            try { return XInputGetStateEx(index, out state); }
            catch (EntryPointNotFoundException) { _exMissing = true; }
        }
        return XInputGetState(index, out state);
    }

    public static bool IsConnected(uint index) => XInputGetState(index, out _) == Success;

    /// <summary>Two body motors, 0..1 each.</summary>
    public static uint SetMotors(uint index, double left, double right)
    {
        static ushort U(double v) => (ushort)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * 65535);
        var v = new Vibration { wLeftMotorSpeed = U(left), wRightMotorSpeed = U(right) };
        return XInputSetState(index, ref v);
    }
}
