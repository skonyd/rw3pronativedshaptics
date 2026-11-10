using System;

namespace RW3ProNativeDSHaptics;

/// <summary>How audio is shared between the two Xbox body motors.</summary>
public enum XboxMotorSplit
{
    /// <summary>By side: left motor ← left channel, right motor ← right channel (default).</summary>
    Stereo,
    /// <summary>By frequency, mono: heavy (left) motor ← bass, light (right) motor ← treble. No left/right.</summary>
    BassTreble
}

/// <summary>
/// Audio → Xbox body-motor envelopes, by SIDE: the left (heavy) motor follows the left channel and the
/// right (light) motor the right channel, each from bass (&lt; 120 Hz) plus <see cref="MidWeight"/> × low-mid
/// (120–400 Hz). The earlier mapping averaged both channels and split by frequency (bass → left motor,
/// mids → right motor), which erased left/right: pans and the left↔right tests felt centred (2026-11-09).
/// Pure (no device), so the contract tests cover it.
/// </summary>
public sealed class XboxBodyEnvelope
{
    public const float MidWeight = 0.7f;
    /// <summary>Stereo widening of the motor pair: mid stays, the left/right difference is multiplied.
    /// Both body motors sit in one rigid shell, so partial pans (diagonals, slides) otherwise feel almost
    /// centred (user report 2026-11-09). 1 = no widening.</summary>
    public const float SideGain = 2.0f;
    /// <summary>Bass/treble split: heavy motor below <see cref="BassHz"/>, light motor above <see cref="TrebleHz"/>.
    /// Treble is quieter in typical audio and the light motor is weaker, hence the weight.</summary>
    public const float BassHz = 120, TrebleHz = 300, TrebleWeight = 1.5f;
    /// <summary>App-wide choice; RW3 sets it to <see cref="XboxMotorSplit.BassTreble"/> only while the controller
    /// is detected in Xbox mode and the user picked it.</summary>
    public static volatile XboxMotorSplit Preferred = XboxMotorSplit.Stereo;
    /// <summary>Overrides <see cref="Preferred"/> for this instance (tests, measurements).</summary>
    public XboxMotorSplit? SplitOverride { get; set; }
    public XboxMotorSplit Split => SplitOverride ?? Preferred;
    private static readonly float Attack = MathF.Exp(-1f / (0.002f * 48000));
    private static readonly float Release = MathF.Exp(-1f / (0.060f * 48000));
    private static readonly float FastRelease = MathF.Exp(-1f / (0.030f * 48000));
    private readonly BiquadLowPass _bassL = new(120), _bassR = new(120), _midL = new(400), _midR = new(400);
    private readonly BiquadLowPass _monoBass = new(BassHz), _monoHigh = new(TrebleHz, highPass: true);   // a true high-pass: m − low-pass leaks bass through the phase shift
    private float _envL, _envR, _envBass, _envTreble;

    /// <summary>Raw per-side envelopes (before widening).</summary>
    public float RawLeft => _envL;
    public float RawRight => _envR;
    public float Left => Split == XboxMotorSplit.BassTreble ? _envBass : Widen(_envL, _envR).L;
    public float Right => Split == XboxMotorSplit.BassTreble ? _envTreble : Widen(_envL, _envR).R;

    /// <summary>Mid/side widening: centred sound unchanged, a hard pan stays one-sided (louder), partial pans spread.</summary>
    public static (float L, float R) Widen(float l, float r, float sideGain = SideGain)
    {
        float mid = 0.5f * (l + r), side = 0.5f * (l - r) * sideGain;
        return (MathF.Max(0, mid + side), MathF.Max(0, mid - side));
    }

    public void Process(float l, float r)
    {
        float bl = _bassL.Process(l), br = _bassR.Process(r);
        float ml = _midL.Process(l) - bl, mr = _midR.Process(r) - br;
        _envL = Follow(_envL, MathF.Abs(bl) + MidWeight * MathF.Abs(ml));
        _envR = Follow(_envR, MathF.Abs(br) + MidWeight * MathF.Abs(mr));
        // Both mappings run all the time, so switching mid-stream has no filter warm-up.
        float m = 0.5f * (l + r);
        float bass = _monoBass.Process(m), high = _monoHigh.Process(m);
        _envBass = Follow(_envBass, MathF.Abs(bass));
        _envTreble = Follow(_envTreble, TrebleWeight * MathF.Abs(high), FastRelease);   // short tail: crisp hits
    }

    public void Reset()
    {
        _envL = _envR = _envBass = _envTreble = 0;
        _bassL.Reset(); _bassR.Reset(); _midL.Reset(); _midR.Reset(); _monoBass.Reset(); _monoHigh.Reset();
    }

    private static float Follow(float env, float x) => Follow(env, x, Release);
    private static float Follow(float env, float x, float release) => x > env ? x + (env - x) * Attack : x + (env - x) * release;
}
