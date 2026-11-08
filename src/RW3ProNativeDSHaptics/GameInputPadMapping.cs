using System;

namespace RW3ProNativeDSHaptics;

public static class GameInputPadMapping
{
    public static bool IsWolverine(ushort vendor, ushort product) => vendor == 0x1532 && product == 0x0A3F;
    private static float Finite(float v) => float.IsFinite(v) ? v : 0;
    private static byte Trigger(float v) => (byte)MathF.Round(Math.Clamp(Finite(v), 0, 1) * 255);
    private static short Stick(float v) => (short)MathF.Round(Math.Clamp(Finite(v), -1, 1) * (v < 0 ? 32768 : 32767));
    public static XInputPad Map(uint buttons, float lt, float rt, float lx, float ly, float rx, float ry)
    {
        ushort b = 0;
        void Bit(uint from, ushort to) { if ((buttons & from) != 0) b |= to; }
        Bit(1, XInputPad.Start); Bit(2, XInputPad.Back);
        Bit(4, XInputPad.A); Bit(8, XInputPad.B); Bit(16, XInputPad.X); Bit(32, XInputPad.Y);
        Bit(64, XInputPad.DpadUp); Bit(128, XInputPad.DpadDown); Bit(256, XInputPad.DpadLeft); Bit(512, XInputPad.DpadRight);
        Bit(1024, XInputPad.LeftShoulder); Bit(2048, XInputPad.RightShoulder);
        Bit(4096, XInputPad.LeftThumb); Bit(8192, XInputPad.RightThumb);
        return new(b, Trigger(lt), Trigger(rt), Stick(lx), Stick(ly), Stick(rx), Stick(ry));
    }
}
