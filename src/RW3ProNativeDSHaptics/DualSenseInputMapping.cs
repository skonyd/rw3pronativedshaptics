using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>Raw XInput state of the physical pad (the reference for the virtual DualSense).</summary>
public readonly record struct XInputPad(ushort Buttons, byte LeftTrigger, byte RightTrigger, short LX, short LY, short RX, short RY)
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
        Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
        LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public bool Has(ushort mask) => (Buttons & mask) != 0;
}

/// <summary>What a game reads from a USB DualSense input report 0x01 (bytes after the id):
/// [0] LX [1] LY [2] RX [3] RY [4] L2 [5] R2 [6] counter, [7] hat (low nibble, 0 = N … 7 = NW, 8 = none)
/// + Square/Cross/Circle/Triangle (bits 4–7), [8] L1 R1 L2 R2 Create Options L3 R3, [9] PS, touchpad, mute.
/// Sticks: 0 = left/up, 0x80 = centre, 0xFF = right/down.</summary>
public readonly record struct DualSenseInputState(
    byte LX, byte LY, byte RX, byte RY, byte L2, byte R2, byte Hat,
    bool Square, bool Cross, bool Circle, bool Triangle,
    bool L1, bool R1, bool Create, bool Options, bool L3, bool R3, bool PS)
{
    public const byte UsbInputReportId = 0x01;
    public const byte HatNone = 8;

    /// <summary>Parses a USB input report including its id byte (as ReadFile returns it).</summary>
    public static bool TryParseUsb(ReadOnlySpan<byte> report, out DualSenseInputState s)
    {
        s = default;
        if (report.Length < 11 || report[0] != UsbInputReportId) return false;
        byte b0 = report[8], b1 = report[9], b2 = report[10];
        s = new DualSenseInputState(report[1], report[2], report[3], report[4], report[5], report[6], (byte)(b0 & 0x0F),
            (b0 & 0x10) != 0, (b0 & 0x20) != 0, (b0 & 0x40) != 0, (b0 & 0x80) != 0,
            (b1 & 0x01) != 0, (b1 & 0x02) != 0, (b1 & 0x10) != 0, (b1 & 0x20) != 0, (b1 & 0x40) != 0, (b1 & 0x80) != 0,
            (b2 & 0x01) != 0);
        return true;
    }
}

/// <summary>
/// XInput (Xbox pad, the reference) → virtual DualSense. One place for the conversion so the bridge,
/// the live readback check and the contract tests all use the same rules.
/// HIDMaestro axes are [0..1]: sticks 0.5 = centre, 0 = left/up (HID Y grows downward, XInput Y grows
/// upward); triggers 0 = released. Same convention as PadForge.
/// </summary>
public static class DualSenseInputMapping
{
    public static float StickX(short v) => (v + 32768f) / 65535f;
    public static float StickY(short v) => (32768f - v) / 65535f;
    public static float Trigger(byte v) => v / 255f;

    /// <summary>The report byte an 8-bit HID axis carries for a [0..1] value.</summary>
    public static byte AxisByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>D-pad → DualSense hat index (0 = N, clockwise, 8 = none). Opposite directions cancel.</summary>
    public static byte Hat(ushort buttons)
    {
        int y = ((buttons & XInputPad.DpadDown) != 0 ? 1 : 0) - ((buttons & XInputPad.DpadUp) != 0 ? 1 : 0);
        int x = ((buttons & XInputPad.DpadRight) != 0 ? 1 : 0) - ((buttons & XInputPad.DpadLeft) != 0 ? 1 : 0);
        return (x, y) switch
        {
            (0, -1) => 0, (1, -1) => 1, (1, 0) => 2, (1, 1) => 3,
            (0, 1) => 4, (-1, 1) => 5, (-1, 0) => 6, (-1, -1) => 7,
            _ => DualSenseInputState.HatNone
        };
    }

    /// <summary>What the game should read for this XInput state (Xbox layout → PlayStation layout:
    /// A = Cross, B = Circle, X = Square, Y = Triangle, View = Create, Menu = Options, Guide = PS).</summary>
    public static DualSenseInputState Expected(in XInputPad p) => new(
        AxisByte(StickX(p.LX)), AxisByte(StickY(p.LY)), AxisByte(StickX(p.RX)), AxisByte(StickY(p.RY)),
        AxisByte(Trigger(p.LeftTrigger)), AxisByte(Trigger(p.RightTrigger)), Hat(p.Buttons),
        Square: p.Has(XInputPad.X), Cross: p.Has(XInputPad.A), Circle: p.Has(XInputPad.B), Triangle: p.Has(XInputPad.Y),
        L1: p.Has(XInputPad.LeftShoulder), R1: p.Has(XInputPad.RightShoulder),
        Create: p.Has(XInputPad.Back), Options: p.Has(XInputPad.Start),
        L3: p.Has(XInputPad.LeftThumb), R3: p.Has(XInputPad.RightThumb), PS: p.Has(XInputPad.Guide));

    /// <summary>Differences a player would notice, e.g. "LY beklenen 128 görülen 0". Axes within
    /// <paramref name="axisTolerance"/> counts are equal (encoder rounding).</summary>
    public static List<string> Differences(in DualSenseInputState expected, in DualSenseInputState actual, int axisTolerance = 2)
    {
        var d = new List<string>();
        void Axis(string name, byte e, byte a) { if (Math.Abs(e - a) > axisTolerance) d.Add($"{name} beklenen {e} görülen {a}"); }
        void Button(string name, bool e, bool a) { if (e != a) d.Add($"{name} beklenen {(e ? "basılı" : "bırakılmış")}"); }
        Axis("LX", expected.LX, actual.LX); Axis("LY", expected.LY, actual.LY);
        Axis("RX", expected.RX, actual.RX); Axis("RY", expected.RY, actual.RY);
        Axis("L2", expected.L2, actual.L2); Axis("R2", expected.R2, actual.R2);
        if (expected.Hat != actual.Hat) d.Add($"D-pad beklenen {expected.Hat} görülen {actual.Hat}");
        Button("Kare", expected.Square, actual.Square); Button("Çarpı", expected.Cross, actual.Cross);
        Button("Daire", expected.Circle, actual.Circle); Button("Üçgen", expected.Triangle, actual.Triangle);
        Button("L1", expected.L1, actual.L1); Button("R1", expected.R1, actual.R1);
        Button("Create", expected.Create, actual.Create); Button("Options", expected.Options, actual.Options);
        Button("L3", expected.L3, actual.L3); Button("R3", expected.R3, actual.R3); Button("PS", expected.PS, actual.PS);
        return d;
    }
}

/// <summary>
/// Live check: does what the game reads from the virtual DualSense match the physical Xbox pad?
/// The bridge records the expected state every input tick; each readback report is accepted when it
/// matches any expected state from the last <see cref="WindowMs"/> (covers latency and stick jitter).
/// A mismatch is only reported after it persists, so a single in-flight frame never trips it.
/// </summary>
public sealed class VirtualPadVerifier
{
    public const double WindowMs = 250;
    public const int MismatchesToReport = 5;
    private readonly (double T, DualSenseInputState S)[] _history = new (double, DualSenseInputState)[128];
    private int _head, _count, _consecutive;
    private readonly object _gate = new();

    public long Checks { get; private set; }
    public long Matches { get; private set; }
    public string? CurrentProblem { get; private set; }
    public string? FirstProblem { get; private set; }

    public void RecordExpected(double timeMs, in DualSenseInputState s)
    {
        lock (_gate)
        {
            if (_count > 0 && _history[(_head - 1 + _history.Length) % _history.Length].S == s) { _history[(_head - 1 + _history.Length) % _history.Length].T = timeMs; return; }
            _history[_head] = (timeMs, s);
            _head = (_head + 1) % _history.Length;
            _count = Math.Min(_count + 1, _history.Length);
        }
    }

    /// <summary>Returns the new problem text when a mismatch has just become persistent, else null.</summary>
    public string? Observe(double timeMs, in DualSenseInputState actual)
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            Checks++;
            List<string>? best = null;
            for (int i = 0; i < _count; i++)
            {
                var (t, s) = _history[(_head - 1 - i + 2 * _history.Length) % _history.Length];
                if (i > 0 && timeMs - t > WindowMs) break;
                var diff = DualSenseInputMapping.Differences(s, actual);
                if (diff.Count == 0) { Matches++; _consecutive = 0; CurrentProblem = null; return null; }
                if (best == null || diff.Count < best.Count) best = diff;
            }
            _consecutive++;
            if (_consecutive != MismatchesToReport) return null;
            CurrentProblem = string.Join(", ", best!);
            FirstProblem ??= CurrentProblem;
            return CurrentProblem;
        }
    }
}
