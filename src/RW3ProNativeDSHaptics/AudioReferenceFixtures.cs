using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

public sealed record AudioReferenceScene(string Id, string Pattern, double Hz, double Level, string Side, int DriveMs = 1200);

/// <summary>Identical, un-equalized system-audio stimuli for Synapse and RW3. Not native/game PCM.</summary>
public static class AudioReferenceFixtures
{
    public const int Rate = 48000, LeadMs = 200, TailMs = 800;
    public static IReadOnlyList<AudioReferenceScene> Scenes()
    {
        var scenes = new List<AudioReferenceScene> { new("silence", "silence", 80, 0, "both") };
        foreach (double hz in new[] { 30d, 40, 50, 60, 80, 100, 120, 150, 190, 250, 300, 350, 400 })
        foreach (double level in new[] { .15, .5, .9 })
        foreach (string side in new[] { "left", "right" })
            scenes.Add(new($"tone-{hz}-{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{side}", "tone", hz, level, side));
        foreach (double hz in new[] { 20d, 600, 1000 }) scenes.Add(new($"outside-{hz}", "tone", hz, .5, "both"));
        foreach (double hz in new[] { 80d, 190, 400 }) scenes.Add(new($"stereo-{hz}", "tone", hz, .5, "both"));
        foreach (string pattern in new[] { "split", "swapped", "opposite", "mixed", "three", "near", "pulse5", "pulse20", "pulse50", "am", "sweep", "reverse-sweep", "noise", "alternating", "dynamics" })
            scenes.Add(new(pattern, pattern, 80, .5, "both", pattern.StartsWith("pulse", StringComparison.Ordinal) ? 1800 : 1200));
        // The initial pilots showed slow/weak short-tone response; retain short cases and add explicit long controls.
        scenes.Add(new("hold60", "tone", 60, .9, "both", 7000));
        scenes.Add(new("hold-hits", "pulse50", 120, .9, "both", 7000));
        return scenes;
    }
    public static IReadOnlyList<AudioReferenceScene> Pilot() =>
    [new("silence", "silence", 80, 0, "both"), new("pilot80", "tone", 80, .5, "both"),
     new("pilot190", "tone", 190, .5, "both"), new("pilot-left", "tone", 80, .5, "left"),
     new("pilot-right", "tone", 80, .5, "right"), new("pilot-hits", "pulse20", 120, .7, "both", 1800)];

    public static float[] Generate(AudioReferenceScene scene)
    {
        if (!double.IsFinite(scene.Hz) || scene.Hz is < 20 or > 1000 || !double.IsFinite(scene.Level) || scene.Level is < 0 or > .9 ||
            scene.DriveMs is < 100 or > 7000 || scene.Side is not ("left" or "right" or "both")) throw new ArgumentOutOfRangeException(nameof(scene));
        var pcm = new float[(LeadMs + scene.DriveMs + TailMs) * 48 * 2];
        var noise = new Random(8713);
        int driveFrames = scene.DriveMs * 48;
        for (int i = 0; i < driveFrames; i++)
        {
            double t = i / (double)Rate, duration = scene.DriveMs / 1000.0;
            double S(double hz) => Math.Sin(2 * Math.PI * hz * t);
            double l = S(scene.Hz), r = l;
            switch (scene.Pattern)
            {
                case "silence": l = r = 0; break;
                case "tone": break;
                case "split": l = S(80); r = S(190); break;
                case "swapped": l = S(190); r = S(80); break;
                case "opposite": r = -l; break;
                case "mixed": l = r = (S(80) + S(190)) / 2; break;
                case "three": l = r = (S(40) + S(120) + S(300)) / 3; break;
                case "near": l = r = (S(80) + S(84)) / 2; break;
                case "pulse5": case "pulse20": case "pulse50":
                    double width = scene.Pattern == "pulse5" ? .005 : scene.Pattern == "pulse20" ? .020 : .050;
                    l = r = t % .3 < width ? S(120) : 0; break;
                case "am": l = r = S(120) * (.5 + .5 * S(8)); break;
                case "sweep": case "reverse-sweep":
                    double start = scene.Pattern == "sweep" ? 30 : 400, end = scene.Pattern == "sweep" ? 400 : 30;
                    l = r = Math.Sin(2 * Math.PI * (start * t + .5 * (end - start) * t * t / duration)); break;
                case "noise": l = r = noise.NextDouble() * 2 - 1; break;
                case "alternating": if ((int)(t / .2) % 2 == 0) r = 0; else l = 0; break;
                case "dynamics":
                    double strength = (int)(t / (duration / 4)) switch { 0 => .15, 1 => .5, 2 => .9, _ => .15 };
                    l = r = S(80) * strength / .9; break;
                default: throw new ArgumentException("Unknown audio-reference pattern.");
            }
            double fade = Math.Clamp(Math.Min(t, duration - t) / .005, 0, 1);
            int at = (LeadMs * 48 + i) * 2;
            pcm[at] = scene.Side == "right" ? 0 : (float)(l * scene.Level * fade);
            pcm[at + 1] = scene.Side == "left" ? 0 : (float)(r * scene.Level * fade);
        }
        return pcm;
    }
}
