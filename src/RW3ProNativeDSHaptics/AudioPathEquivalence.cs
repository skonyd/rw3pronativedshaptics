using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Offline, deterministic check that the PC-mode "Sesten titreşim" path (game audio →
/// <see cref="SensaAudioSink"/> → extractor) gives the engine the SAME features as the Native
/// path (DualSense rear channels → extractor) for the same haptic waveform.
///
/// Native: the waveform is pushed straight into the extractor in 5 ms blocks (as the demo
/// replay / DualSense capture). Audio: the waveform is played at 1/PreGain (the level that
/// reaches the engine at the same strength) and pushed through the sink in 10 ms blocks
/// (loopback period). Features are taken every 33 ms frame on both, sample-exact.
/// </summary>
public static class AudioPathEquivalence
{
    public const int SampleRate = 48000;
    public const int FrameSamples = SampleRate * 33 / 1000;

    public sealed record Signal(string Name, double Seconds, Func<double, (double L, double R)> Wave);

    public sealed record Report(string Name, int Frames, int ActiveFrames,
        double MaxAmpError, double MeanAmpError, double MaxHzError, double MeanHzError,
        int NativeTransients, int AudioTransients, double TransientAmpError,
        int MissingFrames = 0, int FrequencyAvailabilityMismatches = 0,
        int TransientFrameMismatches = 0, double MaxTransientPositionError = 0,
        int InvalidFeatureValues = 0)
    {
        public bool WithinTolerance(double amplitude = .005, double frequencyHz = 1,
            double transientAmplitude = .01, double transientPosition = 0) =>
            Frames > 0 && MissingFrames == 0 && FrequencyAvailabilityMismatches == 0 && TransientFrameMismatches == 0 &&
            InvalidFeatureValues == 0 && MaxAmpError <= amplitude && MaxHzError <= frequencyHz &&
            NativeTransients == AudioTransients && TransientAmpError <= transientAmplitude &&
            MaxTransientPositionError <= transientPosition;

        public override string ToString() =>
            $"{Name,-26} frames {Frames,3} active {ActiveFrames,3} | amp err max {MaxAmpError:F3} mean {MeanAmpError:F4} | " +
            $"Hz err max {MaxHzError,5:F1} mean {MeanHzError,4:F1} | transients N/A {NativeTransients}/{AudioTransients} amp err {TransientAmpError:F2}";
    }

    /// <summary>Feature frames of one path.</summary>
    /// <param name="atAudioLevel">The wave is already at the audio (1/PreGain) level, e.g. a loopback recording.</param>
    public static List<(SideFeatures L, SideFeatures R)> Frames(Signal s, bool audioPath, int nativeDelaySamples = 0, bool atAudioLevel = false, int sourceRate = SampleRate)
    {
        if (audioPath && sourceRate != SampleRate)
        {
            // Audio rendered at the device rate (e.g. 44.1 kHz), converted to 48 kHz like the capture bridge.
            int n0 = (int)(s.Seconds * sourceRate);
            var src = new float[n0 * 2];
            for (int i = 0; i < n0; i++)
            {
                var (l, r) = s.Wave(i / (double)sourceRate);
                src[2 * i] = (float)Math.Clamp(l, -1, 1); src[2 * i + 1] = (float)Math.Clamp(r, -1, 1);
            }
            var rs = new SincResampler(sourceRate, SampleRate);
            var conv = new float[16];
            int got = 0; var all = new List<float>(n0 * 3);
            for (int p = 0; p < n0; p += 441)
            {
                int m = Math.Min(441, n0 - p);
                got = rs.Process(src.AsSpan(2 * p, 2 * m), ref conv);
                for (int i = 0; i < got; i++) all.Add(conv[i]);
            }
            var arr = all.ToArray();
            var resampled = new Signal(s.Name, s.Seconds, t => { int i = (int)Math.Round(t * SampleRate); return 2 * i + 1 < arr.Length ? (arr[2 * i], arr[2 * i + 1]) : (0.0, 0.0); });
            return Frames(resampled, true, 0, atAudioLevel);
        }
        var fx = new HapticFeatureExtractor();
        var sink = audioPath ? new SensaAudioSink(fx) : null;
        long cursor = 0;
        int total = (int)(s.Seconds * SampleRate);
        int block = audioPath ? 480 : 240;
        var buf = new float[block * 2];
        var frames = new List<(SideFeatures, SideFeatures)>();
        int nextFrame = FrameSamples;
        for (int pos = 0; pos < total;)
        {
            int n = Math.Min(block, Math.Min(total - pos, nextFrame - pos));
            for (int i = 0; i < n; i++)
            {
                int src = audioPath ? pos + i : pos + i - nativeDelaySamples;
                var (l, r) = src < 0 ? (0.0, 0.0) : s.Wave(src / (double)SampleRate);
                float fl = (float)Math.Clamp(l, -1, 1), fr = (float)Math.Clamp(r, -1, 1);
                if (atAudioLevel) { fl = (float)l; fr = (float)r; }
                else if (audioPath) { fl /= SensaAudioSink.PreGain; fr /= SensaAudioSink.PreGain; }
                buf[2 * i] = fl; buf[2 * i + 1] = fr;
            }
            if (audioPath) sink!.Accept(buf.AsSpan(0, 2 * n)); else fx.PushStereo(buf.AsSpan(0, 2 * n));
            pos += n;
            if (pos >= nextFrame)
            {
                fx.Take(ref cursor, out var fl, out var fr);
                frames.Add((fl, fr));
                nextFrame += FrameSamples;
            }
        }
        // Do not hide a different ending merely because it is shorter than one 33ms frame.
        if (total > 0 && total % FrameSamples != 0)
        {
            fx.Take(ref cursor, out var fl, out var fr);
            frames.Add((fl, fr));
        }
        return frames;
    }

    public static Report Compare(Signal s, int nativeDelaySamples = 0, int audioSourceRate = SampleRate) =>
        Compare(s.Name, Frames(s, false, nativeDelaySamples), Frames(s, true, 0, false, audioSourceRate));

    /// <summary>Native frames of <paramref name="reference"/> vs audio-path frames of a real recording at audio level.</summary>
    public static Report CompareRecording(Signal reference, Signal recording) =>
        Compare(reference.Name, Frames(reference, false), Frames(recording, true, 0, atAudioLevel: true));

    public static Report CompareFrames(string name, IReadOnlyList<(SideFeatures L, SideFeatures R)> a,
        IReadOnlyList<(SideFeatures L, SideFeatures R)> b) => Compare(name, a, b);

    private static Report Compare(string name, IReadOnlyList<(SideFeatures L, SideFeatures R)> a, IReadOnlyList<(SideFeatures L, SideFeatures R)> b)
    {
        int n = Math.Max(a.Count, b.Count), active = 0, hzCount = 0, nt = 0, at = 0;
        int missing = Math.Abs(a.Count - b.Count), unavailable = 0, transientFrames = 0, invalid = 0;
        double maxAmp = 0, sumAmp = 0, maxHz = 0, sumHz = 0, trErr = 0, positionError = 0; int ampCount = 0;
        for (int i = 0; i < n; i++)
        {
            var af = i < a.Count ? a[i] : default;
            var bf = i < b.Count ? b[i] : default;
            foreach (var (x, y) in new[] { (af.L, bf.L), (af.R, bf.R) })
            {
                float[] px = { x.P0, x.P1, x.P2, x.P3 }, py = { y.P0, y.P1, y.P2, y.P3 };
                double Error(float x0, float y0)
                {
                    if (!float.IsFinite(x0) || !float.IsFinite(y0)) { invalid++; return double.PositiveInfinity; }
                    return Math.Abs(x0 - y0);
                }
                for (int k = 0; k < 4; k++) { double e = Error(px[k], py[k]); maxAmp = Math.Max(maxAmp, e); sumAmp += e; ampCount++; }
                if (x.Peak > 0.05f || y.Peak > 0.05f) active++;
                if (!float.IsFinite(x.FrequencyHz) || !float.IsFinite(y.FrequencyHz) || x.FrequencyHz < 0 || y.FrequencyHz < 0) invalid++;
                if (x.Peak > 1e-4f || y.Peak > 1e-4f)
                {
                    if ((x.FrequencyHz > 0) != (y.FrequencyHz > 0)) unavailable++;
                    double e = Error(x.FrequencyHz, y.FrequencyHz); maxHz = Math.Max(maxHz, e); sumHz += e; hzCount++;
                }
                nt += x.TransientCount; at += y.TransientCount;
                if (x.TransientCount is < 0 or > 2 || y.TransientCount is < 0 or > 2) invalid++;
                if (x.TransientCount != y.TransientCount) transientFrames++;
                if (x.TransientCount > 0 && y.TransientCount > 0)
                {
                    trErr = Math.Max(trErr, Error(x.T0Amplitude, y.T0Amplitude));
                    positionError = Math.Max(positionError, Error(x.T0Position, y.T0Position));
                }
                if (x.TransientCount > 1 && y.TransientCount > 1)
                {
                    trErr = Math.Max(trErr, Error(x.T1Amplitude, y.T1Amplitude));
                    positionError = Math.Max(positionError, Error(x.T1Position, y.T1Position));
                }
            }
        }
        return new Report(name, n, active, maxAmp, ampCount > 0 ? sumAmp / ampCount : 0,
            maxHz, hzCount > 0 ? sumHz / hzCount : 0, nt, at, trErr,
            missing, unavailable, transientFrames, positionError, invalid);
    }

    private static double Sin(double hz, double t) => Math.Sin(2 * Math.PI * hz * t);
    private static double Burst(double t, double on, double ramp = 0.010) =>
        t < 0 || t > on ? 0 : Math.Min(1, Math.Min(t, on - t) / ramp);

    /// <summary>Finite regression grid: selected tones/levels, onsets, L/R, demos and sound tests; not exhaustive physical coverage.</summary>
    public static IEnumerable<Signal> Catalogue()
    {
        foreach (double hz in new double[] { 20, 30, 40, 50, 60, 80, 100, 120, 150, 180, 200, 250, 300, 350, 400 })
            foreach (double amp in new[] { 0.05, 0.2, 0.5, 0.9 })
                yield return new Signal($"tone {hz:F0} Hz @ {amp:F2}", 0.6,
                    t => { double v = amp * Burst(t - 0.05, 0.5) * Sin(hz, t); return (v, 0); });
        foreach (double hz in new double[] { 40, 80, 150, 250, 400 })
            yield return new Signal($"hard hits {hz:F0} Hz", 1.2, t =>
            {
                double local = t % 0.2; double v = 0.9 * Math.Exp(-local / 0.03) * Sin(hz, local);
                return ((int)(t / 0.2) % 2 == 0 ? (v, 0) : (0, v));
            });
        yield return new Signal("level ramp 80 Hz", 1.0, t => (t * Sin(80, t), 0));
        yield return new Signal("right only 120 Hz", 0.6, t => (0, 0.6 * Burst(t - 0.05, 0.5) * Sin(120, t)));
        foreach (var demo in NativeDemoSignals.All)
        {
            var d = demo;
            yield return new Signal("demo " + NativeDemoSignals.Title(d), NativeDemoSignals.DurationMs(d) / 1000.0,
                t => NativeDemoSignals.Sample(d, t));
        }
        foreach (AudioTestKind k in Enum.GetValues(typeof(AudioTestKind)))
        {
            var kind = k;
            yield return new Signal("sound test " + kind, AudioPanTest.DurationOf(kind) / 1000.0,
                t => { var (l, r) = AudioPanTest.Sample(kind, t); return (l * SensaAudioSink.PreGain, r * SensaAudioSink.PreGain); });
        }
    }
}
