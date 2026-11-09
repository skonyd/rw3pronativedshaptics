using System;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Xbox-mode audio → body motors ONLY, using the two-channel XInput command.
/// Does not request audio-derived trigger vibration. XInput can still generate four-channel GIP
/// packets below this API; concurrent game impulse feedback is not guaranteed or mixed here.
///
/// Mapping per 5 ms block:
///   heavy (left) motor  ← bass  (&lt; 120 Hz) envelope, both channels
///   light (right) motor ← low-mid (120–400 Hz) envelope
/// </summary>
public sealed class XboxAudioSink : IAudioHapticSink
{
    private const int Block = 240;                                 // 5 ms at 48 kHz
    private static readonly float Attack = MathF.Exp(-1f / (0.002f * 48000));
    private static readonly float Release = MathF.Exp(-1f / (0.060f * 48000));

    private readonly BiquadLowPass _bassL = new(120), _bassR = new(120), _midL = new(400), _midR = new(400);
    private float _envBassL, _envBassR, _envMid;
    private int _inBlock;
    private int _outputSlot = -1;
    public int PreferredIndex { get; set; } = -1;
    private GamepadVibration _last;
    private long _lastWriteTick;
    private readonly Action<GamepadVibration>? _diagnosticWriter;

    /// <summary>Explicit diagnostic writer tests the real mapping/zero logic without opening a physical gamepad.</summary>
    public XboxAudioSink(Action<GamepadVibration>? diagnosticWriter = null) => _diagnosticWriter = diagnosticWriter;

    public int OutputLevelPercent { get; set; } = 100;
    public float Gain { get; set; } = 2.5f;
    public long NonFiniteSamples { get; private set; }
    public string Description => "Xbox gövde motorları (tetiklere komut gönderilmez)";
    public bool HasGamepad => FindSlot() >= 0;

    private int FindSlot()
    {
        if (PreferredIndex is >= 0 and <= 3) return XInputGetState((uint)PreferredIndex, out _) == 0 ? PreferredIndex : -1;
        int found = -1;
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) { if (found >= 0) return -1; found = (int)i; }
        return found; // ambiguous selection fails closed
    }

    [StructLayout(LayoutKind.Sequential)] private struct BodyVibration { public ushort Left, Right; }
    [StructLayout(LayoutKind.Sequential)] private struct PadState
    { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out PadState state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref BodyVibration vibration);

    public void Accept(ReadOnlySpan<float> stereo48k)
    {
        if (stereo48k.Length % 2 != 0) throw new ArgumentException("Complete stereo frames required.", nameof(stereo48k));
        for (int i = 0; i + 1 < stereo48k.Length; i += 2)
        {
            float l = stereo48k[i], r = stereo48k[i + 1];
            if (!float.IsFinite(l)) { l = 0; NonFiniteSamples++; }
            if (!float.IsFinite(r)) { r = 0; NonFiniteSamples++; }
            float bl = _bassL.Process(l), br = _bassR.Process(r);
            float ml = _midL.Process(l) - bl, mr = _midR.Process(r) - br;
            _envBassL = Follow(_envBassL, MathF.Abs(bl));
            _envBassR = Follow(_envBassR, MathF.Abs(br));
            _envMid = Follow(_envMid, 0.5f * (MathF.Abs(ml) + MathF.Abs(mr)));
            if (++_inBlock == Block) EmitBlock();
        }
    }

    private static float Follow(float env, float x) => x > env ? x + (env - x) * Attack : x + (env - x) * Release;

    private void EmitBlock()
    {
        _inBlock = 0;

        double level = HapticOutputLevel.Apply(1.0, OutputLevelPercent);
        double g = float.IsFinite(Gain) ? Math.Max(0, Gain) : 0;
        var v = new GamepadVibration
        {
            LeftMotor = Clamp(0.5 * (_envBassL + _envBassR) * g) * level,
            RightMotor = Clamp(_envMid * g * 0.8) * level,
            LeftTrigger = 0,
            RightTrigger = 0
        };
        Write(v);
    }

    private void Write(GamepadVibration v)
    {
        bool silent = v.LeftMotor < 1.0 / 65535 && v.RightMotor < 1.0 / 65535;
        bool wasSilent = _last.LeftMotor < 1.0 / 65535 && _last.RightMotor < 1.0 / 65535;
        // Avoid periodic zero packets when RW3 has no body vibration to stop.
        if (silent && wasSilent) return;
        long now = Environment.TickCount64;
        bool changed = Math.Abs(v.LeftMotor - _last.LeftMotor) > 0.02 || Math.Abs(v.RightMotor - _last.RightMotor) > 0.02 ||
                       Math.Abs(v.LeftTrigger - _last.LeftTrigger) > 0.02 || Math.Abs(v.RightTrigger - _last.RightTrigger) > 0.02;
        if (!silent && !changed && now - _lastWriteTick < 100) return;
        if (_diagnosticWriter != null)
        {
            _diagnosticWriter(v); _last = v; _lastWriteTick = now; return;
        }
        int slot = FindSlot();
        if (_outputSlot >= 0 && _outputSlot != slot) { var zero = new BodyVibration(); XInputSetState((uint)_outputSlot, ref zero); }
        _outputSlot = slot;
        if (slot < 0) return;
        var body = new BodyVibration { Left = (ushort)(v.LeftMotor * 65535), Right = (ushort)(v.RightMotor * 65535) };
        if (XInputSetState((uint)slot, ref body) == 0) { _last = v; _lastWriteTick = now; }
    }

    private static double Clamp(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;

    public void Silence()
    {
        _envBassL = _envBassR = _envMid = 0;
        _bassL.Reset(); _bassR.Reset(); _midL.Reset(); _midR.Reset();
        _inBlock = 0;
        if (_last.LeftMotor >= 1.0 / 65535 || _last.RightMotor >= 1.0 / 65535)
        {
            if (_diagnosticWriter != null) _diagnosticWriter(default);
            else { try { if (_outputSlot >= 0) { var zero = new BodyVibration(); XInputSetState((uint)_outputSlot, ref zero); } } catch { } }
        }
        _last = default;
    }
}

/// <summary>Which protocol the Wolverine is using right now.</summary>
public enum WolverineMode { Unknown, Pc, Xbox }

public static class WolverineModeDetector
{
    /// <summary>PC mode = wired 1532:0A4C (Sensa available); Xbox mode = 1532:0A3F (GIP, triggers).</summary>
    public static WolverineMode Detect()
    {
        var usb = UsbGamepadMonitor.Read();
        if (!usb.ScanSucceeded) return WolverineMode.Unknown;
        if (usb.WolverineWiredPcNode) return WolverineMode.Pc;
        if (usb.WolverineUsbNode) return WolverineMode.Xbox;
        return WolverineMode.Unknown;
    }
}
