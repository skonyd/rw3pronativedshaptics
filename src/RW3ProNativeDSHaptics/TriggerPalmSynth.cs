using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// PC mode has no reachable trigger motors, so DualSense trigger effects can be felt in the palms
/// instead: the synth renders them as haptic PCM that Sensa plays like any other haptic signal.
/// <list type="bullet">
/// <item>Click (zone entry, weapon break, bow snap): a short 180 Hz burst with a fast decay.</item>
/// <item>Tension (resistance zone held): a soft 70 Hz texture whose level follows the effect strength,
/// with a 22 Hz grain so it reads as resistance rather than a hum.</item>
/// <item>Vibration effects (0x06, 0x26, 0x27): 120 Hz at the effect amplitude.</item>
/// </list>
/// Left trigger → left palm, right trigger → right palm. Pure (no device); 48 kHz.
/// </summary>
public sealed class TriggerPalmSynth
{
    public const int Rate = 48000;
    public const double ClickHz = 180, TensionHz = 70, GrainHz = 22, VibrationHz = 120;
    public const double ClickGain = 0.85, TensionGain = 0.30, VibrationGain = 0.55;
    public const double ClickDecaySeconds = 0.025;

    private readonly Side _l = new(), _r = new();

    /// <summary>Latest trigger levels (0..1). A new or stronger click level starts a new burst.</summary>
    public void Set(bool left, double click, double tension, double vibration) => (left ? _l : _r).Set(click, tension, vibration);

    /// <summary>True while anything is (or is still decaying) audible.</summary>
    public bool Active => _l.Busy || _r.Busy;

    public void Reset() { _l.Clear(); _r.Clear(); }

    /// <summary>Adds the trigger feel into interleaved stereo float samples (left, right), clamped to ±1.</summary>
    public void Render(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        _l.Render(stereo, 0, frames);
        _r.Render(stereo, 1, frames);
    }

    private static double Clean(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;

    private sealed class Side
    {
        private readonly object _gate = new();
        private double _click, _tension, _vibration, _burst, _burstAge = double.MaxValue;
        private double _pClick, _pTension, _pGrain, _pVib, _tensionLevel, _vibLevel;
        private const double BurstSeconds = ClickDecaySeconds * 6;

        public void Set(double click, double tension, double vibration)
        {
            lock (_gate)
            {
                click = Clean(click); tension = Clean(tension); vibration = Clean(vibration);
                if (click > 0 && (_click <= 0 || click > _click + 1e-6)) { _burst = click; _burstAge = 0; _pClick = 0; }
                _click = click; _tension = tension; _vibration = vibration;
            }
        }

        public bool Busy
        {
            get { lock (_gate) return _tension > 0 || _vibration > 0 || _tensionLevel > 1e-3 || _vibLevel > 1e-3 || _burstAge < BurstSeconds; }
        }

        public void Clear()
        {
            lock (_gate) { _click = _tension = _vibration = _burst = _tensionLevel = _vibLevel = 0; _burstAge = double.MaxValue; }
        }

        public void Render(Span<float> stereo, int channel, int frames)
        {
            lock (_gate)
            {
                const double dt = 1.0 / Rate;
                double smooth = 1 - Math.Exp(-dt / 0.010);   // 10 ms level glide: no clicks from level steps
                for (int i = 0; i < frames; i++)
                {
                    _tensionLevel += (_tension - _tensionLevel) * smooth;
                    _vibLevel += (_vibration - _vibLevel) * smooth;
                    double x = 0;
                    if (_burstAge < BurstSeconds)
                    {
                        x += ClickGain * _burst * Math.Exp(-_burstAge / ClickDecaySeconds) * Math.Sin(_pClick);
                        _pClick += 2 * Math.PI * ClickHz * dt;
                        _burstAge += dt;
                    }
                    if (_tensionLevel > 1e-4) x += TensionGain * _tensionLevel * (0.6 + 0.4 * Math.Sin(_pGrain)) * Math.Sin(_pTension);
                    if (_vibLevel > 1e-4) x += VibrationGain * _vibLevel * Math.Sin(_pVib);
                    _pTension = (_pTension + 2 * Math.PI * TensionHz * dt) % (2 * Math.PI);
                    _pGrain = (_pGrain + 2 * Math.PI * GrainHz * dt) % (2 * Math.PI);
                    _pVib = (_pVib + 2 * Math.PI * VibrationHz * dt) % (2 * Math.PI);
                    int k = 2 * i + channel;
                    stereo[k] = (float)Math.Clamp(stereo[k] + x, -1, 1);
                }
            }
        }
    }
}
