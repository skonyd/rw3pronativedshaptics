using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Hardware-free, allocation-free examination of a 4ch/48k UAC render buffer.
/// Channels 0/1: controller speaker. Channels 2/3: proposed haptic L/R.
/// Neither a 4ch endpoint nor nonzero rear energy proves game-authored output.
/// No PCM is saved or uploaded; source bytes remain available to future backends.
/// </summary>
public static class FourChannelHapticAnalyzer
{
    public const int MaximumSampledFrames = 256;

    public enum PcmEncoding { Pcm16, Float32 }

    public readonly record struct Metrics(
        int ReceivedFrames, int SampledFrames, float SpeakerRms,
        float LeftRms, float RightRms, float LeftPeak, float RightPeak, int NonFiniteSamples)
    {
        public bool HapticEnergyObserved => LeftRms > 0.01f || RightRms > 0.01f;
        public bool SpeakerOnly => SpeakerRms > 0.01f && !HapticEnergyObserved;
    }

    public static bool IsCompatible(int channels, int sampleRate, int bitsPerSample, PcmEncoding encoding) =>
        channels == 4 && sampleRate == 48000 &&
        ((encoding == PcmEncoding.Pcm16 && bitsPerSample == 16) ||
         (encoding == PcmEncoding.Float32 && bitsPerSample == 32));

    public static Metrics Analyze(ReadOnlySpan<byte> buffer, PcmEncoding encoding)
    {
        int bytes = encoding == PcmEncoding.Pcm16 ? 2 : 4;
        int frameBytes = 4 * bytes;
        int frames = buffer.Length / frameBytes;
        if (frames == 0) return default;
        // Bound every callback to <=256 sampled frames, avoiding proportional memory allocation.
        int stride = Math.Max(1, (frames + MaximumSampledFrames - 1) / MaximumSampledFrames);
        double speakerSquares = 0, leftSquares = 0, rightSquares = 0;
        float lp = 0, rp = 0;
        int count = 0, invalid = 0;
        for (int i = 0; i < frames; i += stride)
        {
            int o = i * frameBytes;
            float s0 = ReadSafe(buffer.Slice(o, bytes), encoding, ref invalid);
            float s1 = ReadSafe(buffer.Slice(o + bytes, bytes), encoding, ref invalid);
            float l = ReadSafe(buffer.Slice(o + 2 * bytes, bytes), encoding, ref invalid);
            float r = ReadSafe(buffer.Slice(o + 3 * bytes, bytes), encoding, ref invalid);
            speakerSquares += ((double)s0 * s0 + (double)s1 * s1) / 2;
            leftSquares += (double)l * l;
            rightSquares += (double)r * r;
            lp = Math.Max(lp, Math.Abs(l));
            rp = Math.Max(rp, Math.Abs(r));
            ++count;
        }
        return new Metrics(frames, count,
            (float)Math.Sqrt(speakerSquares / count),
            (float)Math.Sqrt(leftSquares / count),
            (float)Math.Sqrt(rightSquares / count),
            lp, rp, invalid);
    }

    private static float ReadSafe(ReadOnlySpan<byte> sample, PcmEncoding encoding, ref int invalid)
    {
        float value = encoding == PcmEncoding.Pcm16 ?
            BitConverter.ToInt16(sample) / 32768f :
            BitConverter.ToSingle(sample);
        if (!float.IsFinite(value))
        {
            invalid++;
            return 0f;
        }
        return Math.Clamp(value, -1f, 1f);
    }
}
