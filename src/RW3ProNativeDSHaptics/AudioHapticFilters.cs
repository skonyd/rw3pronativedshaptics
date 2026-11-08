using System;

namespace RW3ProNativeDSHaptics;

/// <summary>Receives game audio as interleaved stereo float at 48 kHz.</summary>
public interface IAudioHapticSink
{
    string Description { get; }
    void Accept(ReadOnlySpan<float> stereo48k);
    void Silence();
}

/// <summary>Second-order RBJ low-pass, per channel, allocation free.</summary>
public sealed class BiquadLowPass
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public BiquadLowPass(double cutoffHz, double sampleRate = 48000, double q = 0.7071)
    {
        double w = 2 * Math.PI * cutoffHz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double a0 = 1 + alpha;
        _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = _b0;
        _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
    }

    public float Process(float x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return (float)y;
    }

    public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }
}

/// <summary>Butterworth low-pass of order 2×sections as a cascade of RBJ biquads.</summary>
public sealed class ButterworthLowPass
{
    private readonly BiquadLowPass[] _stages;

    public ButterworthLowPass(double cutoffHz, int order, double sampleRate = 48000)
    {
        int sections = order / 2;
        _stages = new BiquadLowPass[sections];
        for (int k = 0; k < sections; k++)
        {
            // Pole pair k of a Butterworth filter: Q = 1 / (2 sin((2k+1)π / 2n)).
            double q = 1.0 / (2 * Math.Sin((2 * k + 1) * Math.PI / (2.0 * order)));
            _stages[k] = new BiquadLowPass(cutoffHz, sampleRate, q);
        }
    }

    public float Process(float x)
    {
        foreach (var s in _stages) x = s.Process(x);
        return x;
    }

    public void Reset() { foreach (var s in _stages) s.Reset(); }

    /// <summary>Ideal magnitude response |H(f)|.</summary>
    public static double Magnitude(double hz, double cutoffHz, int order) => 1 / Math.Sqrt(1 + Math.Pow(hz / cutoffHz, 2 * order));
}

/// <summary>
/// PC-mode "Sesten titreşim": game audio → gain → the same v2 extractor/engine as Native.
/// Left/right stay separate. The haptic band limit lives in the extractor and is applied to both
/// paths identically. Equalized inputs are compared on a finite regression grid, frame by
/// frame for selected tones 20–400 Hz, onsets, demos and sound tests by
/// <see cref="AudioPathEquivalence"/>. This does not establish arbitrary audio or physical fidelity;
/// Default unity input gain preserves full-scale audio headroom. Explicit higher diagnostic gains
/// can clip and are counted; the separate Wolverine output gain/ceiling remains lossy.
/// 2026-11-07: the old private 2× 2nd-order 220 Hz filter here passed 150 Hz at 0.82, 250 Hz at
/// 0.36 and 400 Hz at 0.08 — amplitude error up to 0.76 vs Native.
/// </summary>
public sealed class SensaAudioSink : IAudioHapticSink
{
    public const float PreGain = 1f;
    public const float LegacyPreGain = 2.2f;
    private readonly HapticFeatureExtractor _target;
    private float[] _buffer = new float[4096];
    public long InputSamples { get; private set; }
    public long ClippedSamples { get; private set; }
    public long NonFiniteSamples { get; private set; }
    public float InputGain { get; }

    public SensaAudioSink(HapticFeatureExtractor target, float inputGain = PreGain)
    {
        if (!float.IsFinite(inputGain) || inputGain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(inputGain));
        _target = target; InputGain = inputGain;
    }
    public string Description => "Sensa (sol/sağ, v2)";

    public void Accept(ReadOnlySpan<float> stereo48k)
    {
        if (stereo48k.Length % 2 != 0) throw new ArgumentException("Complete stereo frames required.", nameof(stereo48k));
        if (_buffer.Length < stereo48k.Length) _buffer = new float[stereo48k.Length];
        for (int i = 0; i < stereo48k.Length; i++)
        {
            float value = stereo48k[i] * InputGain;
            if (!float.IsFinite(value)) { NonFiniteSamples++; _buffer[i] = 0; continue; }
            if (Math.Abs(value) > 1) ClippedSamples++;
            _buffer[i] = Math.Clamp(value, -1f, 1f);
        }
        InputSamples += stereo48k.Length & ~1;
        _target.PushStereo(_buffer.AsSpan(0, stereo48k.Length & ~1));
    }

    public void Silence() => _target.ClearForSilence();
}

/// <summary>Audio -> haptics sync delay policy (see <see cref="AudioHapticsBridge"/>).</summary>
public static class AudioSyncDelay
{
    /// <summary>Heuristic from one Bluetooth setup; not a measured per-device synchronisation guarantee.</summary>
    public const int BluetoothAutoMs = 300, MaxMs = 1000;

    /// <summary>setting -1 = automatic (Bluetooth -> <see cref="BluetoothAutoMs"/>, wired -> 0), otherwise ms clamped to 0..<see cref="MaxMs"/>.</summary>
    public static int Resolve(int setting, bool bluetooth) =>
        setting < 0 ? (bluetooth ? BluetoothAutoMs : 0) : Math.Clamp(setting, 0, MaxMs);
}
