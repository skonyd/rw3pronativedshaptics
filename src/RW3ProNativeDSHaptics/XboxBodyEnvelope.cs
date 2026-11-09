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
    private static readonly float Attack = MathF.Exp(-1f / (0.002f * 48000));
    private static readonly float Release = MathF.Exp(-1f / (0.060f * 48000));
    private readonly BiquadLowPass _bassL = new(120), _bassR = new(120), _midL = new(400), _midR = new(400);
    private float _envL, _envR;

    public float Left => _envL;
    public float Right => _envR;

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
