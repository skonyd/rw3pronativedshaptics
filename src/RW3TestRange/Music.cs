namespace RW3TestRange;

/// <summary>
/// Endless background music, synthesised (no files): 100 BPM, A minor (Am – F – C – G), 8-bar loop.
/// Drums (kick, snare, hats) are pre-rendered hits; bass, pad and lead are band-limited (polyBLEP) saws through
/// filters, with note envelopes (no clicks), a ping-pong delay on the lead and a room on snare / pad / lead.
/// Its bass line and lead melody are what RW3's audio-to-haptics and melody mode should pick up.
/// </summary>
public sealed class MusicGenerator
{
    private const double Bpm = 100;
    private const int Rate = Synth.Rate;
    private static readonly int StepSamples = (int)(Rate * 60.0 / Bpm / 4);   // 16th note
    public float Volume = .5f;

    // A minor: Am, F, C, G (pad voicings, MIDI) and bass roots
    private static readonly int[][] Chords = { new[] { 57, 60, 64 }, new[] { 53, 57, 60 }, new[] { 55, 60, 64 }, new[] { 55, 59, 62 } };
    private static readonly int[] Roots = { 33, 29, 36, 31 };
    private static readonly int[] BassSteps = { 0, 0, -1, 0, 12, -1, 0, -1, 0, 0, -1, 7, 12, -1, 7, -1 };   // semitones over the root, -1 = rest
    // lead: two bars of A minor pentatonic, -1 = rest
    private static readonly int[] Lead =
    {
        76, -1, 74, -1, 72, -1, 74, 76, -1, -1, 79, -1, 76, -1, 74, -1,
        72, -1, 69, -1, 72, 74, -1, 72, 69, -1, -1, -1, 67, -1, 69, -1
    };

    private readonly List<(float[] Data, int Pos, float Gain, float Pan, float Send)> _hits = new();
    private readonly float[] _kick, _snare, _hat, _openHat;
    private readonly Reverb _room = new(1.1) { Feedback = .8f, Damp = .45f };
    private readonly float[] _delayL = new float[(int)(Rate * .45)], _delayR = new float[(int)(Rate * .45)];
    private int _delayPos;
    private long _n;
    private int _lastStep = -1;

    // bass voice
    private double _bassPh, _bassHz = 55, _bassEnv; private readonly Biquad _bassLp = Biquad.LowPass(400, 1.1);
    // pad: 3 notes × 2 detuned saws, crossfading envelope
    private readonly double[] _padPh = new double[6]; private double[] _padHz = new double[3]; private double _padEnv;
    private readonly Biquad _padLpL = Biquad.LowPass(1300), _padLpR = Biquad.LowPass(1300);
    // lead
    private double _leadPh, _leadHz = 440, _leadTarget = 440, _leadEnv, _vib; private bool _leadOn;
    private readonly Biquad _leadLp = Biquad.LowPass(2600);

    public MusicGenerator()
    {
        _kick = Dsp.Add(Dsp.Sweep(.35, 160, 48, .025, 9), Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.01), 1), Biquad.HighPass(3000)), 400), .3f);
        _snare = Dsp.Add(Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.25), 2), Biquad.BandPass(1900, .7)), 17), Dsp.Sweep(.15, 220, 180, .02, 28), .6f);
        _hat = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.05), 3), Biquad.HighPass(7500)), 75);
        _openHat = Dsp.Decay(Dsp.Filter(Dsp.Noise(Dsp.N(.3), 4), Biquad.HighPass(6500)), 11);
        _padHz = Chords[0].Select(Midi).ToArray();
    }

    private static double Midi(int m) => 440 * Math.Pow(2, (m - 69) / 12.0);

    private static double PolyBlep(double t, double dt)
    {
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }

    private static float Saw(ref double ph, double hz)
    {
        double dt = hz / Rate;
        ph += dt; if (ph >= 1) ph -= 1;
        return (float)(2 * ph - 1 - PolyBlep(ph, dt));
    }

    private void OnStep(int step)
    {
        int s = step % 16, bar = step / 16 % 8, chord = bar % 4;
        // drums: kick 1 & 3 (+ a pickup), snare 2 & 4, 8th hats, open hat before the bar line
        if (s is 0 or 8 || s == 6 && bar % 2 == 1 || s == 14 && bar == 7) _hits.Add((_kick, 0, .95f, 0, 0));
        if (s is 4 or 12) _hits.Add((_snare, 0, .55f, .05f, .25f));
        if (bar == 7 && s is 13 or 15) _hits.Add((_snare, 0, .3f, .1f, .25f));
        if (s % 2 == 0 && s != 14) _hits.Add((_hat, 0, s % 4 == 0 ? .16f : .1f, .3f, 0));
        if (s == 14) _hits.Add((_openHat, 0, .12f, .3f, .1f));
        // bass
        int b = BassSteps[s];
        if (b >= 0) { _bassHz = Midi(Roots[chord] + b); _bassEnv = 1; }
        // pad follows the chord each bar
        if (s == 0) _padHz = Chords[chord].Select(Midi).ToArray();
        // lead in bars 2-3 and 6-7
        if (bar is 2 or 3 or 6 or 7)
        {
            int note = Lead[(bar % 2) * 16 + s];
            if (note >= 0) { _leadTarget = Midi(note); if (!_leadOn) _leadHz = _leadTarget; _leadOn = true; _leadEnv = 1; }
        }
        else _leadOn = false;
    }

    public void Read(float[] buffer, int offset, int frames)
    {
        float vol = Volume;
        for (int f = 0; f < frames; f++, _n++)
        {
            int step = (int)(_n / StepSamples);
            if (step != _lastStep) { _lastStep = step; OnStep(step); }
            double t = _n / (double)Rate;
            float l = 0, r = 0, send = 0;

            for (int h = _hits.Count - 1; h >= 0; h--)
            {
                var (d, pos, g, pan, snd) = _hits[h];
                float x = d[pos] * g;
                l += x * (1 - Math.Max(0, pan)); r += x * (1 + Math.Min(0, pan));
                send += x * snd;
                if (++pos >= d.Length) _hits.RemoveAt(h); else _hits[h] = (d, pos, g, pan, snd);
            }

            // bass: saw, envelope-driven low-pass (pluck)
            _bassEnv *= .99985;
            if (_n % 32 == 0) _bassLp.SetLowPass(180 + 900 * _bassEnv * _bassEnv, 1.2);
            float bass = _bassLp.Process(Saw(ref _bassPh, _bassHz)) * (float)(.35 + .65 * _bassEnv) * .42f;
            l += bass; r += bass;

            // pad: slow swell each bar, slightly detuned pairs spread left/right
            int stepInBar = step % 16;
            double target = stepInBar < 15 ? 1 : .55;
            _padEnv += (target - _padEnv) * .00006;
            float pl = 0, pr = 0;
            for (int k = 0; k < 3; k++)
            {
                pl += Saw(ref _padPh[2 * k], _padHz[k] * .997);
                pr += Saw(ref _padPh[2 * k + 1], _padHz[k] * 1.003);
            }
            pl = _padLpL.Process(pl) * (float)_padEnv * .055f; pr = _padLpR.Process(pr) * (float)_padEnv * .055f;
            l += pl; r += pr; send += (pl + pr) * .6f;

            // lead: glide + vibrato, decaying notes, ping-pong delay
            float lead = 0;
            if (_leadOn || _leadEnv > .001)
            {
                _leadHz += (_leadTarget - _leadHz) * .002;
                _vib += 2 * Math.PI * 5.2 / Rate;
                _leadEnv *= .99992;
                double hz = _leadHz * (1 + .004 * Math.Sin(_vib) * Math.Min(1, (1 - _leadEnv) * 4));
                lead = _leadLp.Process(Saw(ref _leadPh, hz) * .6f) * (float)(_leadEnv * .16);
            }
            float dl = _delayL[_delayPos], dr = _delayR[_delayPos];
            _delayL[_delayPos] = lead + dr * .38f;
            _delayR[_delayPos] = dl * .38f;
            if (++_delayPos == _delayL.Length) _delayPos = 0;
            l += lead + dl * .35f; r += lead * .8f + dr * .35f;
            send += lead * .5f;

            var (wl, wr) = _room.Process(send);
            l += wl * .5f; r += wr * .5f;

            buffer[offset + 2 * f] += Dsp.Soft(l * vol);
            buffer[offset + 2 * f + 1] += Dsp.Soft(r * vol);
        }
    }
}
