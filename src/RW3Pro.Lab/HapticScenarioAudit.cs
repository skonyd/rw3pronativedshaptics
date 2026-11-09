using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

/// <summary>Finite stress/oracle observations. No physical output or fidelity verdict.</summary>
public static class HapticScenarioAudit
{
    public sealed record GainObservation(double InputGain, double InputAmplitude, long Samples, long ClippedSamples, double ClippedFraction);
    public sealed record RepresentationObservation(string Name, double RawRmsDifference,
        AudioPathEquivalence.Report Whole, AudioPathEquivalence.Report Steady,
        string Limitation);
    public sealed record MultitoneObservation(double Original80HzAmplitude, double Original190HzAmplitude,
        int SimultaneousOriginalTones, int AvailableCarrierFrequenciesPerSide, string Limitation);

    public static MultitoneObservation MultitoneLoss()
    {
        // Independent synchronous projections of original samples; never compare extractor to itself.
        double Project(double hz)
        {
            double sin = 0, cos = 0;
            for (int i = 0; i < 48000; i++)
            {
                double t = i / 48000d;
                double sample = .25 * Math.Sin(2 * Math.PI * 80 * t) + .25 * Math.Sin(2 * Math.PI * 190 * t);
                sin += sample * Math.Sin(2 * Math.PI * hz * t);
                cos += sample * Math.Cos(2 * Math.PI * hz * t);
            }
            return 2 * Math.Sqrt(sin * sin + cos * cos) / 48000;
        }
        return new(Project(80), Project(190), 2, 1,
            "The original has two simultaneous spectral components; current SideFeatures carries one frequency plus envelope/transients. This is a representation limitation, not a measurement of the actuator spectrum or a claim about harmonics.");
    }

    public static IEnumerable<GainObservation> GainGrid()
    {
        foreach (float gain in new[] { SensaAudioSink.PreGain, SensaAudioSink.LegacyPreGain })
        foreach (double level in new[] { .05, .2, .4, 1d / 2.2, .5, .7, .9, 1 })
        {
            var sink = new SensaAudioSink(new HapticFeatureExtractor(), gain);
            var samples = new float[48000 * 2];
            for (int i = 0; i < 48000; i++) samples[2 * i] = (float)(level * Math.Sin(2 * Math.PI * 80 * i / 48000));
            sink.Accept(samples);
            // Right is intentionally silent; percentage denominator is the 48k active-channel samples.
            yield return new(gain, level, 48000, sink.ClippedSamples, sink.ClippedSamples / 48000d);
        }
    }

    public static IEnumerable<AudioPathEquivalence.Signal> StressSignals()
    {
        double Gate(double t) => t is >= .05 and < .45 ? 1 : 0;
        foreach (double offsetMs in new[] { 0d, .25, .5, 1, 7, 16, 31, 32, 32.75 })
        foreach (double widthMs in new[] { .1, .25, .5, 1, 2, 5, 10, 20 })
        {
            double onset = .066 + offsetMs / 1000;
            yield return new($"pulse-{offsetMs}-{widthMs}", .2, t =>
                t >= onset && t < onset + widthMs / 1000 ? (.5, 0) : (0, 0));
        }
        yield return new("multitone-80-190", .5, t => (Gate(t) * (.25 * Math.Sin(2 * Math.PI * 80 * t) + .25 * Math.Sin(2 * Math.PI * 190 * t)), 0));
        yield return new("three-tone-40-120-300", .5, t => (Gate(t) * (.2 * Math.Sin(2 * Math.PI * 40 * t) + .2 * Math.Sin(2 * Math.PI * 120 * t) + .2 * Math.Sin(2 * Math.PI * 300 * t)), 0));
        yield return new("beat-80-84", .5, t => (Gate(t) * (.25 * Math.Sin(2 * Math.PI * 80 * t) + .25 * Math.Sin(2 * Math.PI * 84 * t)), 0));
        yield return new("opposite-stereo-phase", .5, t => { double v = Gate(t) * .5 * Math.Sin(2 * Math.PI * 80 * t); return (v, -v); });
        yield return new("quadrature-stereo", .5, t => (Gate(t) * .5 * Math.Sin(2 * Math.PI * 80 * t), Gate(t) * .5 * Math.Cos(2 * Math.PI * 80 * t)));
        yield return new("dc-offset-software-only", .5, t => (Gate(t) * (.2 + .3 * Math.Sin(2 * Math.PI * 120 * t)), 0));
        yield return new("full-scale-software-only", .5, t => (Gate(t) * Math.Sin(2 * Math.PI * 120 * t), 0));
        yield return new("out-of-band-1k-software-only", .5, t => (Gate(t) * .5 * Math.Sin(2 * Math.PI * 1000 * t), 0));
    }

    public static RepresentationObservation PhaseLoss()
    {
        var a = new AudioPathEquivalence.Signal("phase-positive", 1, t => (.5 * Math.Sin(2 * Math.PI * 80 * t), 0));
        var b = new AudioPathEquivalence.Signal("phase-inverted", 1, t => (-.5 * Math.Sin(2 * Math.PI * 80 * t), 0));
        var af = AudioPathEquivalence.Frames(a, false); var bf = AudioPathEquivalence.Frames(b, false);
        // Independently calculate original PCM difference, not extractor outputs.
        double error = Math.Sqrt(Enumerable.Range(0, 48000).Average(i =>
        { double delta = a.Wave(i / 48000d).L - b.Wave(i / 48000d).L; return delta * delta; }));
        return new("polarity inversion", error,
            AudioPathEquivalence.CompareFrames("whole", af, bf),
            AudioPathEquivalence.CompareFrames("steady-330-to-660ms", af.Skip(10).Take(10).ToArray(), bf.Skip(10).Take(10).ToArray()),
            "Raw PCM differs even if steady features match: the parametric representation has no signed waveform phase. No physical-fidelity success is inferred.");
    }
}
