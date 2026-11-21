using System.Runtime.InteropServices;

namespace RW3TestRange;

/// <summary>XInput (xinput1_4): the classic Xbox path most PC games use. Reading + the two body motors.</summary>
public sealed class XInputPad : IPadSource
{
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] private struct Vibration { public ushort Left, Right; }
    [StructLayout(LayoutKind.Sequential)] private struct CapsEx { public byte Type, SubType; public ushort Flags, Buttons; public byte LT, RT; public short LX, LY, RX, RY; public ushort WL, WR, Vid, Pid, Version, Unk1; public uint Unk2; }
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint GetStateEx(uint user, out State s);   // includes the Guide button
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint user, out State s);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint user, ref Vibration v);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] private static extern uint GetCapsEx(uint one, uint user, uint flags, out CapsEx c);

    public uint Slot { get; }
    public XInputPad(uint slot) => Slot = slot;
    public string Kind => "xinput";
    public bool Sony => false;
    public string Name
    {
        get
        {
            string id = "";
            try { if (GetCapsEx(1, Slot, 0, out var c) == 0) id = $" {c.Vid:X4}:{c.Pid:X4}"; } catch { }
            return $"XInput yuva {Slot}{id}";
        }
    }
    public bool Connected => XInputGetState(Slot, out _) == 0;
    public static IEnumerable<XInputPad> Present() { for (uint i = 0; i < 4; i++) { var p = new XInputPad(i); if (p.Connected) yield return p; } }

    public bool TryRead(out PadState s)
    {
        s = default;
        State st;
        uint r;
        try { r = GetStateEx(Slot, out st); } catch (EntryPointNotFoundException) { r = XInputGetState(Slot, out st); }
        if (r != 0) return false;
        var g = st.Pad;
        Btn b = Btn.None;
        void M(int bit, Btn f) { if ((g.Buttons & bit) != 0) b |= f; }
        M(0x0001, Btn.Up); M(0x0002, Btn.Down); M(0x0004, Btn.Left); M(0x0008, Btn.Right); M(0x0010, Btn.Menu); M(0x0020, Btn.View);
        M(0x0040, Btn.LS); M(0x0080, Btn.RS); M(0x0100, Btn.LB); M(0x0200, Btn.RB); M(0x0400, Btn.Guide);
        M(0x1000, Btn.A); M(0x2000, Btn.B); M(0x4000, Btn.X); M(0x8000, Btn.Y);
        s.Buttons = b;
        s.LX = g.LX / 32767f; s.LY = g.LY / 32767f; s.RX = g.RX / 32767f; s.RY = g.RY / 32767f;
        s.LT = g.LT / 255f; s.RT = g.RT / 255f;
        s.DeriveTriggerButtons();
        return true;
    }

    /// <summary>Left = big (low-frequency) motor, right = small (high-frequency) motor, 0..1.</summary>
    public bool Rumble(float left, float right)
    {
        var v = new Vibration { Left = (ushort)(Math.Clamp(left, 0, 1) * 65535), Right = (ushort)(Math.Clamp(right, 0, 1) * 65535) };
        return XInputSetState(Slot, ref v) == 0;
    }

    public void Dispose() { try { Rumble(0, 0); } catch { } }
}
