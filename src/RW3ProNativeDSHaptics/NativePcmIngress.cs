using System;

namespace RW3ProNativeDSHaptics;

/// <summary>The same rear-channel decoder boundary for WASAPI input and labelled offline replay.</summary>
public static class NativePcmIngress
{
    public static void Push(ReadOnlySpan<byte> pcm, FourChannelHapticAnalyzer.PcmEncoding encoding,
        HapticFeatureExtractor extractor)
    {
        int frameBytes = encoding switch
        {
            FourChannelHapticAnalyzer.PcmEncoding.Pcm16 => 8,
            FourChannelHapticAnalyzer.PcmEncoding.Float32 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
        if (pcm.Length % frameBytes != 0)
            throw new ArgumentException("Incomplete 4-channel PCM frame", nameof(pcm));
        extractor.Push(pcm, encoding); // Reads rear channels only; never edits original PCM.
    }
}
