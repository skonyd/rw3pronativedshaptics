using System;
using System.Linq;
using Windows.Gaming.Input;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Xbox-mode "Sesten titreşim": game audio → main motors + impulse-trigger motors through
/// Windows.Gaming.Input (the only Windows API with trigger channels). Measured 2026-11-07:
/// in Xbox mode (GIP, 1532:0A3F) LeftTrigger/RightTrigger reach the trigger motors; in PC
/// mode (XUSB, 0A4C) WGI vibration does nothing.
///
/// Mapping per 5 ms block:
///   heavy (left) motor  ← bass  (&lt; 120 Hz) envelope, both channels
///   light (right) motor ← low-mid (120–400 Hz) envelope
///   left/right trigger  ← sharp bass hits on that side (decaying pulse) + a little sustained bass
/// </summary>
public sealed class XboxAudioSink : IAudioHapticSink
{
    private const int Block = 240;                                 // 5 ms at 48 kHz
    private static readonly float Attack = MathF.Exp(-1f / (0.002f * 48000));
    private static readonly float Release = MathF.Exp(-1f / (0.060f * 48000));

    private readonly BiquadLowPass _bassL = new(120), _bassR = new(120), _midL = new(400), _midR = new(400);
    private float _envBassL, _envBassR, _envMid, _longL = 1e-5f, _longR = 1e-5f, _pulseL, _pulseR;
    private float _blockEnergyL, _blockEnergyR;
    private int _inBlock;
    private Gamepad? _pad;
    private GamepadVibration _last;
    private long _lastWriteTick;
    private readonly Action<GamepadVibration>? _diagnosticWriter;

    /// <summary>Explicit diagnostic writer tests the real mapping/zero logic without opening a physical gamepad.</summary>
    public XboxAudioSink(Action<GamepadVibration>? diagnosticWriter = null) => _diagnosticWriter = diagnosticWriter;

    public int OutputLevelPercent { get; set; } = 100;
    public float Gain { get; set; } = 2.5f;
    public long NonFiniteSamples { get; private set; }
    public string Description => "Xbox ana motorlar + tetikler";
    public bool HasGamepad => Pad() != null;

    private Gamepad? Pad()
    {
        if (_pad != null && Gamepad.Gamepads.Contains(_pad)) return _pad;
        _pad = Gamepad.Gamepads.FirstOrDefault();
        return _pad;
    }

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
            _blockEnergyL += bl * bl; _blockEnergyR += br * br;
            if (++_inBlock == Block) EmitBlock();
        }
    }

    private static float Follow(float env, float x) => x > env ? x + (env - x) * Attack : x + (env - x) * Release;

    private void EmitBlock()
    {
        float eL = _blockEnergyL / Block, eR = _blockEnergyR / Block;
        // Onset of a bass hit on a side → trigger pulse on that side (max one per ~60 ms via decay).
        if (eL > 2e-4f && eL > _longL * 5f && _pulseL < 0.3f) _pulseL = Math.Min(1f, MathF.Sqrt(eL) * 4f);
        if (eR > 2e-4f && eR > _longR * 5f && _pulseR < 0.3f) _pulseR = Math.Min(1f, MathF.Sqrt(eR) * 4f);
        _longL = _longL * 0.95f + eL * 0.05f; _longR = _longR * 0.95f + eR * 0.05f;
        _pulseL *= 0.92f; _pulseR *= 0.92f;                        // ≈ 60 ms decay at 5 ms blocks
        _blockEnergyL = _blockEnergyR = 0; _inBlock = 0;

        double level = HapticOutputLevel.Apply(1.0, OutputLevelPercent);
        double g = Gain * level;
        var v = new GamepadVibration
        {
            LeftMotor = Clamp(0.5 * (_envBassL + _envBassR) * g),
            RightMotor = Clamp(_envMid * g * 0.8),
            LeftTrigger = Clamp((_pulseL + 0.25 * _envBassL * Gain) * level),
            RightTrigger = Clamp((_pulseR + 0.25 * _envBassR * Gain) * level)
        };
        Write(v);
    }

    private void Write(GamepadVibration v)
    {
        long now = Environment.TickCount64;
        bool changed = Math.Abs(v.LeftMotor - _last.LeftMotor) > 0.02 || Math.Abs(v.RightMotor - _last.RightMotor) > 0.02 ||
                       Math.Abs(v.LeftTrigger - _last.LeftTrigger) > 0.02 || Math.Abs(v.RightTrigger - _last.RightTrigger) > 0.02;
        if (!changed && now - _lastWriteTick < 100) return;
        if (_diagnosticWriter != null)
        {
            _diagnosticWriter(v); _last = v; _lastWriteTick = now; return;
        }
        var pad = Pad();
        if (pad == null) return;
        try { pad.Vibration = v; _last = v; _lastWriteTick = now; }
        catch (Exception ex) { DiagnosticsService.Error("WGI vibration", ex); _pad = null; }
    }

    private static double Clamp(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;

    public void Silence()
    {
        _envBassL = _envBassR = _envMid = _pulseL = _pulseR = 0;
        _bassL.Reset(); _bassR.Reset(); _midL.Reset(); _midR.Reset();
        _blockEnergyL = _blockEnergyR = 0; _inBlock = 0;
        _longL = _longR = 1e-5f;
        if (_diagnosticWriter != null) _diagnosticWriter(default);
        else { try { var pad = Pad(); if (pad != null) pad.Vibration = new GamepadVibration(); } catch { } }
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
