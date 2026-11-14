using System;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Xbox-mode audio → body motors ONLY, using the two-channel XInput command.
/// Does not request audio-derived trigger vibration. XInput can still generate four-channel GIP
/// packets below this API; concurrent game impulse feedback is not guaranteed or mixed here.
///
/// Mapping per 5 ms block, by SIDE (the motors sit in the left and right grips):
///   left (heavy) motor  ← left channel:  bass (&lt; 120 Hz) + 0.7 × low-mid (120–400 Hz) envelope
///   right (light) motor ← right channel: the same
/// The earlier mapping averaged both channels and split by frequency (bass → left, mids → right),
/// which erased left/right: pans and the left↔right tests felt centred (2026-11-09).
/// </summary>
public sealed class XboxAudioSink : IAudioHapticSink
{
    private const int Block = 240;                                 // 5 ms at 48 kHz
    private readonly XboxBodyEnvelope _env = new();
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
    /// <summary>App-wide audio vibration level (UI slider, percent), read per block so it applies live.</summary>
    public static volatile int LevelPercent = 100;
    public long NonFiniteSamples { get; private set; }
    public string Description => "Xbox gövde motorları (tetiklere komut gönderilmez)";
    public bool HasGamepad => FindSlot() >= 0;

    private int FindSlot()
    {
        if (PreferredIndex is >= 0 and <= 3) return XInput.IsConnected((uint)PreferredIndex) ? PreferredIndex : -1;
        int found = -1;
        for (uint i = 0; i < 4; i++)
            if (XInput.IsConnected(i)) { if (found >= 0) return -1; found = (int)i; }
        return found; // ambiguous selection fails closed
    }


    public void Accept(ReadOnlySpan<float> stereo48k)
    {
        if (stereo48k.Length % 2 != 0) throw new ArgumentException("Complete stereo frames required.", nameof(stereo48k));
        for (int i = 0; i + 1 < stereo48k.Length; i += 2)
        {
            float l = stereo48k[i], r = stereo48k[i + 1];
            if (!float.IsFinite(l)) { l = 0; NonFiniteSamples++; }
            if (!float.IsFinite(r)) { r = 0; NonFiniteSamples++; }
            _env.Process(l, r);
            if (++_inBlock == Block) EmitBlock();
        }
    }

    private void EmitBlock()
    {
        _inBlock = 0;

        double level = HapticOutputLevel.Apply(1.0, OutputLevelPercent);
        double g = (float.IsFinite(Gain) ? Math.Max(0, Gain) : 0) * Math.Clamp(LevelPercent, 0, 400) / 100.0;
        var v = new GamepadVibration
        {
            LeftMotor = Clamp(_env.Left * g) * level,
            RightMotor = Clamp(_env.Right * g) * level,
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
        if (_outputSlot >= 0 && _outputSlot != slot) { XInput.SetMotors((uint)_outputSlot, 0, 0); }
        _outputSlot = slot;
        if (slot < 0) return;
        if (XInput.SetMotors((uint)slot, v.LeftMotor, v.RightMotor) == XInput.Success) { _last = v; _lastWriteTick = now; }
    }

    private static double Clamp(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;

    public void Silence()
    {
        _env.Reset();
        _inBlock = 0;
        if (_last.LeftMotor >= 1.0 / 65535 || _last.RightMotor >= 1.0 / 65535)
        {
            if (_diagnosticWriter != null) _diagnosticWriter(default);
            else { try { if (_outputSlot >= 0) { XInput.SetMotors((uint)_outputSlot, 0, 0); } } catch { } }
        }
        _last = default;
    }
}

/// <summary>Which protocol the Wolverine is using right now.</summary>
public enum WolverineMode { Unknown, Pc, Xbox }

public static class WolverineModeDetector
{
    /// <summary>PC mode = 1532:0A4C (cable or HyperSpeed dongle; Sensa available); Xbox mode = 1532:0A3F
    /// (GIP, trigger motors). One implementation: <see cref="WolverineModeControl.Detect"/>.</summary>
    public static WolverineMode Detect() => WolverineModeControl.Detect() switch
    {
        WolverineControllerMode.Pc => WolverineMode.Pc,
        WolverineControllerMode.Xbox => WolverineMode.Xbox,
        _ => WolverineMode.Unknown
    };
}
