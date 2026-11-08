using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace RW3ProNativeDSHaptics;

public sealed record ChannelLabScene(string Id, string Title, NativeDemo? Demo = null)
{
    public override string ToString() => Title;
}

public sealed record ChannelSourceAnalysis(int Frames, float SpeakerRms, float LeftRms, float RightRms,
    float LeftPeak, float RightPeak, long FeaturePoints, long SaturatedFeaturePoints,
    float LeftFeaturePeak, float RightFeaturePeak);

/// <summary>Project-authored 4ch/48k PCM16, not captured or licensed game assets.</summary>
public static class DualSenseChannelFixtures
{
    public static IReadOnlyList<ChannelLabScene> Scenes { get; } = CreateScenes();
    private static IReadOnlyList<ChannelLabScene> CreateScenes()
    {
        var list = new List<ChannelLabScene>
        {
            new("left", "Kanal 3 • yalnız sol"), new("right", "Kanal 4 • yalnız sağ"),
            new("stereo", "Stereo • sol 80 Hz / sağ 190 Hz"),
            new("alternating", "Sol → sessizlik → sağ"), new("transients", "Kısa vuruşlar"),
            new("speaker", "Negatif • yalnız ön hoparlör"), new("silence", "Negatif • tam sessizlik")
        };
        foreach (var demo in NativeDemoSignals.All)
            list.Add(new("demo:" + demo, NativeDemoSignals.Title(demo), demo));
        return list.AsReadOnly();
    }

    public static byte[] Build(ChannelLabScene scene)
    {
        int ms = scene.Demo is { } demo ? NativeDemoSignals.DurationMs(demo) : 3000;
        var pcm = new byte[ms * 48 * 8];
        if (scene.Demo is { } selected)
        {
            uint seed = 0x1234567; // One canonical source, shared by every output route.
            NativeDemoSignals.Fill(selected, 0, pcm, ref seed);
            return pcm;
        }
        if (scene.Id is not ("left" or "right" or "stereo" or "alternating" or "transients" or "speaker" or "silence"))
            throw new ArgumentException("Unknown channel fixture", nameof(scene));
        for (int frame = 0; frame < ms * 48; frame++)
        {
            double t = frame / 48000.0;
            // 250ms silence at both ends; 5ms fades on every gated burst.
            double local = t - 0.25;
            if (local < 0 || t >= 2.75) continue;
            double taper = Math.Clamp(Math.Min(local, 2.75 - t) / 0.005, 0, 1);
            double left = 0, right = 0, speaker = 0;
            if (scene.Id == "left") left = Math.Sin(2 * Math.PI * 80 * t);
            if (scene.Id == "right") right = Math.Sin(2 * Math.PI * 190 * t);
            if (scene.Id == "stereo") { left = Math.Sin(2 * Math.PI * 80 * t); right = Math.Sin(2 * Math.PI * 190 * t); }
            if (scene.Id == "alternating")
            {
                if (t < 1.1) { left = Math.Sin(2 * Math.PI * 80 * t); taper *= Math.Clamp((1.1 - t) / .005, 0, 1); }
                if (t >= 1.6) { right = Math.Sin(2 * Math.PI * 190 * t); taper *= Math.Clamp((t - 1.6) / .005, 0, 1); }
            }
            if (scene.Id == "transients")
            {
                double phase = local % .4;
                if (phase < .04)
                {
                    taper *= Math.Clamp(Math.Min(phase, .04 - phase) / .005, 0, 1);
                    if (((int)(local / .4) & 1) == 0) left = Math.Sin(2 * Math.PI * 120 * t);
                    else right = Math.Sin(2 * Math.PI * 200 * t);
                }
            }
            if (scene.Id == "speaker") speaker = Math.Sin(2 * Math.PI * 150 * t);
            Write(pcm, frame * 8, speaker * .22 * taper);
            Write(pcm, frame * 8 + 2, speaker * .22 * taper);
            Write(pcm, frame * 8 + 4, left * .22 * taper);
            Write(pcm, frame * 8 + 6, right * .22 * taper);
        }
        return pcm;
    }

    private static void Write(byte[] pcm, int offset, double value)
    {
        short sample = (short)Math.Round(value * short.MaxValue);
        pcm[offset] = (byte)sample; pcm[offset + 1] = (byte)(sample >> 8);
    }
    public static string Sha256(byte[] pcm) => Convert.ToHexString(SHA256.HashData(pcm));

    /// <summary>Software-only prediction; never a sensor or physical-output measurement.</summary>
    public static ChannelSourceAnalysis Analyze(byte[] pcm, int level, int gain)
    {
        var extractor = new HapticFeatureExtractor();
        long cursor = 0, points = 0, saturated = 0;
        int frames = 0;
        double speakerSquares = 0, leftSquares = 0, rightSquares = 0;
        float lp = 0, rp = 0, sourceLp = 0, sourceRp = 0;
        double scale = HapticOutputLevel.Apply(gain / 100.0, level);
        for (int offset = 0; offset < pcm.Length; offset += 240 * 8)
        {
            var block = pcm.AsSpan(offset, Math.Min(240 * 8, pcm.Length - offset));
            var metrics = FourChannelHapticAnalyzer.Analyze(block, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
            frames += metrics.ReceivedFrames;
            speakerSquares += (double)metrics.SpeakerRms * metrics.SpeakerRms * metrics.ReceivedFrames;
            leftSquares += (double)metrics.LeftRms * metrics.LeftRms * metrics.ReceivedFrames;
            rightSquares += (double)metrics.RightRms * metrics.RightRms * metrics.ReceivedFrames;
            sourceLp = Math.Max(sourceLp, metrics.LeftPeak); sourceRp = Math.Max(sourceRp, metrics.RightPeak);
            NativePcmIngress.Push(block,
                FourChannelHapticAnalyzer.PcmEncoding.Pcm16, extractor);
            extractor.Take(ref cursor, out var left, out var right);
            lp = Math.Max(lp, left.Peak); rp = Math.Max(rp, right.Peak);
            Count(left); Count(right);
        }
        return new(frames, frames == 0 ? 0 : (float)Math.Sqrt(speakerSquares / frames),
            frames == 0 ? 0 : (float)Math.Sqrt(leftSquares / frames), frames == 0 ? 0 : (float)Math.Sqrt(rightSquares / frames),
            sourceLp, sourceRp, points, saturated, lp, rp);

        void Count(SideFeatures f)
        {
            points += 4;
            if (f.P0 * scale > 1) saturated++;
            if (f.P1 * scale > 1) saturated++;
            if (f.P2 * scale > 1) saturated++;
            if (f.P3 * scale > 1) saturated++;
        }
    }
}
