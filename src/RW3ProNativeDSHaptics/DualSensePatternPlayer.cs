using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Plays the same 3-beat test pattern as the Wolverine side test, but on a USB DualSense:
/// 4ch/48 kHz PCM16 where channels 0/1 (speaker) stay silent and the rear haptic channels
/// 2 (left) / 3 (right) carry a short sine burst per beat. It is a human reference for
/// "how much left/right leak feels normal", not game-authored data.
/// </summary>
public static class DualSensePatternPlayer
{
    public const int SampleRate = 48000;
    public const double CarrierHz = 150;     // inside the 65–300 Hz band used for Wolverine
    public const double Amplitude = 0.8;

    public static byte[] BuildPattern(bool left, bool right)
    {
        int on = SampleRate * SensaStereoPrototype.PatternOnMs / 1000;
        int off = SampleRate * SensaStereoPrototype.PatternOffMs / 1000;
        int beats = SensaStereoPrototype.PatternBeats;
        int frames = beats * on + (beats - 1) * off;
        byte[] pcm = new byte[frames * 8];              // 4 x PCM16
        int fade = SampleRate / 200;                    // 5 ms edges, no clicks
        for (int beat = 0; beat < beats; beat++)
        {
            int start = beat * (on + off);
            for (int i = 0; i < on; i++)
            {
                double taper = Math.Clamp(Math.Min(i, on - i - 1) / (double)fade, 0, 1);
                short v = (short)Math.Round(Math.Sin(i * 2 * Math.PI * CarrierHz / SampleRate) *
                                            Amplitude * taper * short.MaxValue);
                int pos = (start + i) * 8;
                if (left) { pcm[pos + 4] = (byte)v; pcm[pos + 5] = (byte)(v >> 8); }
                if (right) { pcm[pos + 6] = (byte)v; pcm[pos + 7] = (byte)(v >> 8); }
            }
        }
        return pcm;
    }

    public static Task PlayAsync(string endpointId, bool left, bool right, CancellationToken token) =>
        PlayPcm16Async(endpointId, BuildPattern(left, right), token);

    /// <summary>Plays interleaved 4ch PCM16 (rear 2/3 = haptics) unchanged on the DualSense using Exclusive mode only.</summary>
    public static async Task PlayPcm16Async(string endpointId, byte[] pcm16, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        var mix = device.AudioClient.MixFormat;
        if (mix.Channels != 4 || mix.SampleRate != SampleRate)
            throw new InvalidOperationException("DualSense testi 4 kanal / 48 kHz ses aygıtı gerektirir.");
        // Strict reference: no conversion, gain or automatic Shared fallback.
        var quad = DualSenseAudioFormat.QuadPcm16();
        using var source = DualSenseAudioFormat.OpenReferenceStream(pcm16,
            device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, quad));
        using var output = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 50);
        output.Init(source);
        int ms = pcm16.Length / 8 * 1000 / SampleRate;
        try
        {
            output.Play();
            await Task.Delay(ms + 150, token);
        }
        finally { output.Stop(); }
    }
}
