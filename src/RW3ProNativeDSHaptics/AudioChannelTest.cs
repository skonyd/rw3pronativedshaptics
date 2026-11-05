using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Plays REAL, short stereo audio signals to the SAME WASAPI endpoint currently captured.
/// Only exercises Audio-to-conventional-XInput mode; never emits DualSense PCM.
/// </summary>
public static class AudioChannelTest
{
    public static async Task PlayAsync(string endpointId, HapticTestChannel side, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        const int sampleRate = 48000;
        const int durationMs = 470;
        const int channels = 2;
        const int bits = 16;
        var buffer = BuildStereoTone(side, sampleRate, durationMs);
        using var memory = new MemoryStream(buffer, writable: false);
        using var source = new RawSourceWaveStream(memory, new WaveFormat(sampleRate, bits, channels));
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 75);
        player.Init(source);
        try
        {
            player.Play();
            await Task.Delay(durationMs + 250, token);
        }
        finally
        {
            player.Stop();
        }
    }

    // Immutable function: generated samples are fed to Windows output, not directly into
    // the vibration driver. Actual WASAPI capture must observe the resulting waveform.
    public static byte[] BuildStereoTone(HapticTestChannel side, int sampleRate, int durationMs)
    {
        if (sampleRate is < 8000 or > 192000 || durationMs is < 50 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Invalid tone parameters.");
        int frames = checked(sampleRate * durationMs / 1000);
        var bytes = new byte[frames * 4]; // PCM16 stereo
        double frequency = side == HapticTestChannel.Left ? 135 : 205;
        int fade = sampleRate * 20 / 1000;
        for (int i = 0; i < frames; ++i)
        {
            double edge = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)Math.Max(1, fade));
            short sample = (short)Math.Round(Math.Sin(2 * Math.PI * frequency * i / sampleRate)
                * 0.23 * edge * short.MaxValue);
            int offset = i * 4 + (side == HapticTestChannel.Right ? 2 : 0);
            bytes[offset] = (byte)sample;
            bytes[offset + 1] = (byte)(sample >> 8);
        }
        return bytes;
    }
}
