using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Audio mode: optional level shaping of the captured stereo signal before it becomes vibration.
/// <list type="bullet">
/// <item><b>Auto level</b>: follows the haptic-band (&lt; 250 Hz) level of the last ~3.5 s and pulls it toward
/// <see cref="Target"/>, so the vibration depends on the content rather than the volume knob. Gain ×0.5..×4.
/// Turning down is fast (0.4 s), turning up slow (3.5 s), so a sudden blast cannot be inflated. Near silence
/// the gain is held (noise is never boosted).</item>
/// <item><b>Hum suppression</b>: a part of the signal that stays high for seconds (music bed, engine drone) is
/// slowly reduced, down to <see cref="HumFloor"/>. A hit rises far above that slow baseline in milliseconds,
/// so it passes at full level and stands out from the drone.</item>
/// </list>
/// One gain for both channels (left/right balance is kept). Pure (no device); 48 kHz stereo.
/// </summary>
public sealed class AudioLevelShaper
{
    public const int Rate = 48000;
    /// <summary>Haptic-band RMS the auto level aims for (≈ -20 dBFS).</summary>
    public const float Target = 0.10f;
    public const float MinGain = 0.5f, MaxGain = 4f;
    /// <summary>Below this haptic-band RMS the content counts as silence and the gain is held.</summary>
    public const float SilenceRms = 0.004f;
    /// <summary>Lowest hum gain: a steady drone ends up at 40 % of its level.</summary>
    public const float HumFloor = 0.4f;
    /// <summary>The fast level must exceed the drone baseline by this fraction (+50 %) to open the hum gain fully.</summary>
    public const float HitRatio = 0.5f;
    public const float LevelUpSeconds = 3.5f, LevelDownSeconds = 0.4f, HumBaselineSeconds = 2.5f;

    /// <summary>App-wide settings (Settings page); read for every block, so a change applies live.</summary>
    public static volatile bool AutoLevelEnabled, HumSuppressionEnabled;

    /// <summary>
    /// Standard ("DSX gibi", default since 2026-11-14): the audio becomes vibration as it is. No gain, no
    /// compression, no auto level, no hum stage: a quiet sound gives a weak vibration, as in DSX, whose audio-to-haptics
    /// profile has only a per-motor intensity (its saved profile: <c>audio_haptics_left/rightmotor_intensity</c>).
    /// The one addition is a soft noise gate on the haptic band, so near-silence (room tone, a quiet music bed after
    /// volume compensation) gives no vibration at all instead of a constant faint hum. When on, it overrides
    /// <see cref="AutoLevelEnabled"/> and <see cref="HumSuppressionEnabled"/>. User report 2026-11-14: with auto level +
    /// hum suppression on, audio mode "vibrated all the time and never stopped".
    /// </summary>
    public static volatile bool StandardEnabled = true;

    /// <summary>Haptic-band envelope (≈ -30 dBFS) below which the standard gate closes.</summary>
    public const float GateThreshold = 0.03f;
    /// <summary>Gate opens in 1 ms (hits keep their attack) and the envelope releases over 80 ms (no chatter).</summary>
    public const float GateAttackSeconds = 0.001f, GateReleaseSeconds = 0.080f;
    private static readonly float GateAttack = Coef(GateAttackSeconds), GateRelease = Coef(GateReleaseSeconds), GateGlide = Coef(0.005f);
    private float _gateEnv, _gate;

    /// <summary>Current standard-gate gain 0..1 (for status and tests).</summary>
    public float GateGain => _gate;

    private readonly BiquadLowPass _bandL = new(250), _bandR = new(250);
    private static readonly float PowerUp = Coef(LevelUpSeconds), PowerDown = Coef(LevelDownSeconds);
    private static readonly float FastAttack = Coef(0.002f), FastRelease = Coef(0.060f);
    private static readonly float BaseRise = Coef(HumBaselineSeconds), BaseFall = Coef(0.150f), GainGlide = Coef(0.020f), HumOpen = Coef(0.002f), MeanPower = Coef(0.050f);
    private float _mean = Target * Target, _power = Target * Target, _gain = 1f, _fast, _base, _hum = 1f;

    /// <summary>Current auto-level gain and hum gain (for status and tests).</summary>
    public float LevelGain => _gain;
    public float HumGain => _hum;

    public void Reset()
    {
        _bandL.Reset(); _bandR.Reset();
        _mean = _power = Target * Target; _gain = 1f; _fast = _base = 0; _hum = 1f;
        _gateEnv = 0; _gate = 0;
    }

    /// <summary>Processes interleaved stereo in place with the app-wide settings.</summary>
    public void Process(Span<float> stereo)
    {
        if (StandardEnabled) ProcessStandard(stereo);
        else Process(stereo, AutoLevelEnabled, HumSuppressionEnabled);
    }

    /// <summary>
    /// Standard mode: unity gain above <see cref="GateThreshold"/>; below it a downward expander (gain = (env/T)²,
    /// so 6 dB under the threshold comes out 18 dB lower) fades the rest to nothing. One gain for both channels.
    /// </summary>
    public void ProcessStandard(Span<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float l = stereo[i], r = stereo[i + 1];
            if (!float.IsFinite(l)) l = 0;
            if (!float.IsFinite(r)) r = 0;
            float bl = _bandL.Process(l), br = _bandR.Process(r);
            float band = MathF.Max(MathF.Abs(bl), MathF.Abs(br));
            _gateEnv = band > _gateEnv ? band + (_gateEnv - band) * GateAttack : band + (_gateEnv - band) * GateRelease;
            float ratio = _gateEnv / GateThreshold;
            float want = ratio >= 1f ? 1f : ratio * ratio;
            _gate = want > _gate ? want : want + (_gate - want) * GateGlide;   // open at once, close over ~5 ms
            stereo[i] = Math.Clamp(l * _gate, -1f, 1f);
            stereo[i + 1] = Math.Clamp(r * _gate, -1f, 1f);
        }
        _gain = 1f; _hum = 1f;
    }

    public void Process(Span<float> stereo, bool autoLevel, bool humSuppression)
    {
        if (!autoLevel && !humSuppression) { _gain = 1f; _hum = 1f; return; }
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float l = stereo[i], r = stereo[i + 1];
            if (!float.IsFinite(l)) l = 0;
            if (!float.IsFinite(r)) r = 0;
            float bl = (float)_bandL.Process(l), br = (float)_bandR.Process(r);
            float band = MathF.Max(MathF.Abs(bl), MathF.Abs(br));

            float gain = 1f;
            if (autoLevel)
            {
                _mean = band * band + (_mean - band * band) * MeanPower;   // 50 ms mean power: no ripple from the waveform
                float p = _mean;
                // Power follower: up fast (louder content turns the gain down quickly), down slowly.
                _power = p > _power ? p + (_power - p) * PowerDown : p + (_power - p) * PowerUp;
                float rms = MathF.Sqrt(_power);
                float want = rms < SilenceRms ? _gain : Math.Clamp(Target / rms, MinGain, MaxGain);
                _gain = want + (_gain - want) * GainGlide;
                gain = _gain;
            }
            else _gain = 1f;

            if (humSuppression)
            {
                float x = band * gain;
                _fast = x > _fast ? x + (_fast - x) * FastAttack : x + (_fast - x) * FastRelease;
                // Baseline: climbs over seconds, drops quickly when the drone stops.
                _base = _fast > _base ? _fast + (_base - _fast) * BaseRise : _fast + (_base - _fast) * BaseFall;
                // Steady (fast ≈ baseline) → floor; fast ≥ 1.5 × baseline (a hit, or a new sound) → fully open.
                float rise = _base > 1e-5f ? (_fast / _base - 1f) / HitRatio : 1f;
                float want = HumFloor + (1f - HumFloor) * Math.Clamp(rise, 0f, 1f);
                _hum = want + (_hum - want) * (want > _hum ? HumOpen : GainGlide);   // opens in 2 ms for a hit
                gain *= _hum;
            }
            else _hum = 1f;

            stereo[i] = Math.Clamp(l * gain, -1f, 1f);
            stereo[i + 1] = Math.Clamp(r * gain, -1f, 1f);
        }
    }

    private static float Coef(float seconds) => MathF.Exp(-1f / (seconds * Rate));
}
