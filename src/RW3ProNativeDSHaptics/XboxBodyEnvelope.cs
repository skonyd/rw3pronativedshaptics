using System;

namespace RW3ProNativeDSHaptics;

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
    private static readonly float Attack = MathF.Exp(-1f / (0.002f * 48000));
    private static readonly float Release = MathF.Exp(-1f / (0.060f * 48000));
    private readonly BiquadLowPass _bassL = new(120), _bassR = new(120), _midL = new(400), _midR = new(400);
    private float _envL, _envR;

    /// <summary>Raw per-side envelopes (before widening).</summary>
    public float RawLeft => _envL;
    public float RawRight => _envR;
    public float Left => Widen(_envL, _envR).L;
    public float Right => Widen(_envL, _envR).R;

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
    }

    public void Reset()
    {
        _envL = _envR = 0;
        _bassL.Reset(); _bassR.Reset(); _midL.Reset(); _midR.Reset();
    }

    private static float Follow(float env, float x) => x > env ? x + (env - x) * Attack : x + (env - x) * Release;
}
