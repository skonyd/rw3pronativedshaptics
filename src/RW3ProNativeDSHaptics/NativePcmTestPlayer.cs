using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// End-to-end game-free 4-channel UAC loopback fixture:
/// speaker channels 0/1 silent, haptic channel 2 or 3 receives a quiet tone.
/// Synthetic test data is NEVER represented as a game's authored DualSense PCM.
/// </summary>
public static class NativePcmTestPlayer
{
    public static async Task PlayAsync(string endpointId, HapticTestChannel side, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        var format = device.AudioClient.MixFormat;
        if (format.Channels != 4 || format.SampleRate != 48000)
            throw new InvalidOperationException(
                "Synthetic test requires an exact 4-channel 48 kHz UAC endpoint.");
        const int sampleRate = 48000;
        const int durationMs = 520;
        byte[] pcm = BuildFourChannelTone(side, sampleRate, durationMs);
        using var memory = new MemoryStream(pcm, writable: false);
        using var source = new RawSourceWaveStream(memory, new WaveFormat(sampleRate, 16, 4));
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(source);
        try
        {
            output.Play();
            await Task.Delay(durationMs + 200, token);
        }
        finally { output.Stop(); }
    }

    public static byte[] BuildFourChannelTone(HapticTestChannel side, int sampleRate, int durationMs)
    {
        if (sampleRate != 48000 || durationMs is < 100 or > 1500)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int frames = sampleRate * durationMs / 1000;
        byte[] pcm = new byte[frames * 8]; // 4 x PCM16 channels
        int channel = side == HapticTestChannel.Left ? 2 : 3;
        int fade = sampleRate / 50;
        double freq = side == HapticTestChannel.Left ? 132 : 194;
        for (int i = 0; i < frames; i++)
        {
            double taper = Math.Clamp(Math.Min(i, frames - i - 1) / (double)fade, 0, 1);
            short sample = (short)Math.Round(Math.Sin(i * (2 * Math.PI * freq / sampleRate)) *
                0.15 * taper * short.MaxValue);
            int pos = i * 8 + channel * 2;
            pcm[pos] = (byte)sample;
            pcm[pos + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    public static bool VerifyIsolatedChannel(HapticTestChannel channel)
    {
        var pcm = BuildFourChannelTone(channel, 48000, 520);
        long[] energy = new long[4];
        for (int i = 0; i < pcm.Length; i += 8)
            for (int c = 0; c < 4; c++)
            {
                short v = (short)(pcm[i + c * 2] | (pcm[i + c * 2 + 1] << 8));
                energy[c] += (long)v * v;
            }
        int expected = channel == HapticTestChannel.Left ? 2 : 3;
        for (int c = 0; c < 4; c++)
            if ((c == expected && energy[c] == 0) || (c != expected && energy[c] != 0))
                return false;
        return true;
    }
}
