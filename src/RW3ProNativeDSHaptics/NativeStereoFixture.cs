using System;
using System.IO;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Deterministic SYNTHETIC stereo PCM reference signal, not game-authored
/// DualSense commands. It does NOT address Wolverine hardware by itself.
/// </summary>
public static class NativeStereoFixture
{
    public const int SampleRate = 48000;
    public const int DurationMs = 600;

    public static (float[] Left, float[] Right) Generate(HapticTestChannel channel)
    {
        int frames = SampleRate * DurationMs / 1000;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)SampleRate;
            double envelope = Math.Min(1, Math.Min(i / 1200.0, (frames - i - 1) / 1200.0));
            envelope = Math.Max(0, envelope);
            if (channel == HapticTestChannel.Left)
                left[i] = (float)(0.22 * envelope *
                    (Math.Sin(2 * Math.PI * 130 * t) + 0.18 * Math.Sin(2 * Math.PI * 61 * t)) / 1.18);
            else
                right[i] = (float)(0.22 * envelope *
                    (Math.Sin(2 * Math.PI * 210 * t) + 0.20 * Math.Sin(2 * Math.PI * 87 * t)) / 1.2);
        }
        return (left, right);
    }

    public static string ExportWav(HapticTestChannel channel)
    {
        var (left, right) = Generate(channel);
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RW3ProNativeDSHaptics", "test-fixtures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory,
            channel == HapticTestChannel.Left ? "synthetic-left-48k.wav" : "synthetic-right-48k.wav");
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        int dataBytes = left.Length * 4;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < left.Length; ++i)
        {
            writer.Write((short)Math.Round(left[i] * short.MaxValue));
            writer.Write((short)Math.Round(right[i] * short.MaxValue));
        }
        return path;
    }

    public static bool ValidateSeparation(HapticTestChannel channel)
    {
        var (left, right) = Generate(channel);
        double leftEnergy = 0, rightEnergy = 0;
        for (int i = 0; i < left.Length; ++i)
        {
            leftEnergy += left[i] * left[i];
            rightEnergy += right[i] * right[i];
        }
        return channel == HapticTestChannel.Left
            ? leftEnergy > 10 && rightEnergy == 0
            : rightEnergy > 10 && leftEnergy == 0;
    }
}
