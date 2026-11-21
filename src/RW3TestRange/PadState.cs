namespace RW3TestRange;

/// <summary>Buttons in one set for every pad type (Xbox names; PlayStation names in <see cref="Buttons.Label"/>).</summary>
[Flags]
public enum Btn : uint
{
    None = 0, A = 1, B = 2, X = 4, Y = 8, LB = 16, RB = 32, View = 64, Menu = 128, LS = 256, RS = 512,
    Up = 1024, Down = 2048, Left = 4096, Right = 8192, Guide = 16384, Touchpad = 32768, Mute = 65536, LTd = 131072, RTd = 262144
}

public static class Buttons
{
    public static readonly Btn[] All =
        { Btn.A, Btn.B, Btn.X, Btn.Y, Btn.LB, Btn.RB, Btn.LTd, Btn.RTd, Btn.View, Btn.Menu, Btn.LS, Btn.RS,
          Btn.Up, Btn.Down, Btn.Left, Btn.Right, Btn.Guide, Btn.Touchpad, Btn.Mute };

    public static string Label(Btn b, bool sony) => b switch
    {
        Btn.A => sony ? "✕" : "A", Btn.B => sony ? "○" : "B", Btn.X => sony ? "□" : "X", Btn.Y => sony ? "△" : "Y",
        Btn.LB => sony ? "L1" : "LB", Btn.RB => sony ? "R1" : "RB", Btn.LTd => sony ? "L2" : "LT", Btn.RTd => sony ? "R2" : "RT",
        Btn.View => sony ? "Create" : "View ⧉", Btn.Menu => sony ? "Options" : "Menü ☰", Btn.LS => sony ? "L3" : "LS", Btn.RS => sony ? "R3" : "RS",
        Btn.Up => "▲", Btn.Down => "▼", Btn.Left => "◀", Btn.Right => "▶", Btn.Guide => sony ? "PS" : "Xbox",
        Btn.Touchpad => "Touchpad", Btn.Mute => "Mute", _ => b.ToString()
    };

    /// <summary>Buttons a pad family has (so the button test does not wait for buttons that do not exist).</summary>
    public static bool Exists(Btn b, bool sony) => sony || b is not (Btn.Touchpad or Btn.Mute);
}

/// <summary>One reading in the game's own units: sticks -1..1 (up = +), triggers 0..1.</summary>
public struct PadState
{
    public Btn Buttons;
    public float LX, LY, RX, RY, LT, RT;
    public bool HasMotion;
    public float GyroX, GyroY, GyroZ, AccX, AccY, AccZ;
    public bool Touching;
    public float TouchX, TouchY;

    public readonly bool Down(Btn b) => (Buttons & b) != 0;

    public readonly bool SameAs(in PadState o) =>
        Buttons == o.Buttons && MathF.Abs(LX - o.LX) < .02f && MathF.Abs(LY - o.LY) < .02f && MathF.Abs(RX - o.RX) < .02f &&
        MathF.Abs(RY - o.RY) < .02f && MathF.Abs(LT - o.LT) < .02f && MathF.Abs(RT - o.RT) < .02f && Touching == o.Touching;

    /// <summary>Digital L2/R2 from the analog values (half pull), so the button test covers them too.</summary>
    public void DeriveTriggerButtons()
    {
        if (LT > .5f) Buttons |= Btn.LTd;
        if (RT > .5f) Buttons |= Btn.RTd;
    }

    public static float Dead(float v, float dead = .12f) => MathF.Abs(v) < dead ? 0 : (v - MathF.Sign(v) * dead) / (1 - dead);
}

/// <summary>Something the game reads a pad from (XInput slot, GameInput device, DualSense HID).</summary>
public interface IPadSource : IDisposable
{
    string Name { get; }
    /// <summary>"xinput", "gameinput", "dualsense".</summary>
    string Kind { get; }
    bool Sony { get; }
    bool Connected { get; }
    bool TryRead(out PadState state);
}
