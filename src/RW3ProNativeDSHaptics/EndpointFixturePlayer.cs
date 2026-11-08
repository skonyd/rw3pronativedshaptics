using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

public sealed record EndpointEmission(string EndpointId, string EndpointName, string Mode,
    string OriginalSha256, string RenderSha256, int Frames, int BitsPerSample, float EndpointVolume, bool EndpointMuted);

/// <summary>Explicit Shared-mode synthetic source for loopback testing; never an Exclusive reference fallback.</summary>
public static class EndpointFixturePlayer
{
    public static async Task<int> RunCommandAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--emit-channel-fixture") return 2;
        try
        {
            var scene = System.Linq.Enumerable.Single(DualSenseChannelFixtures.Scenes, s => s.Id == args[2]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            var emission = await PlayAsync(args[1], scene, timeout.Token);
            await File.WriteAllTextAsync(args[3], System.Text.Json.JsonSerializer.Serialize(emission));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(args[3], System.Text.Json.JsonSerializer.Serialize(new { Failure = ex.Message }));
            return 1;
        }
    }

    public static byte[] EncodeSource(byte[] pcm16, WaveFormat format)
    {
        if (format.Channels != 4 || format.SampleRate != 48000 || pcm16.Length == 0 || pcm16.Length % 8 != 0)
            throw new NotSupportedException("Synthetic endpoint source requires complete 4ch/48k PCM16 frames.");
        if (format is WaveFormatExtensible ext && DualSenseAudioFormat.ChannelMask(ext) != DualSenseAudioFormat.QuadChannelMask)
            throw new NotSupportedException("Endpoint channel mask must be quad 0x33; centre/LFE routing is not accepted.");
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ef && ef.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (floating && format.BitsPerSample == 32)
        {
            // Every PCM16 value has an exact Float32 representation. No gain/normalization/resampling.
            var data = new byte[pcm16.Length * 2];
            for (int i = 0; i < pcm16.Length / 2; i++)
                BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), BitConverter.ToInt16(pcm16, i * 2) / 32768f);
            return data;
        }
        bool integer = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible ep && ep.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (integer && format.BitsPerSample == 16) return pcm16;
        throw new NotSupportedException("Endpoint mix format must be PCM16 or Float32.");
    }

    public static async Task<EndpointEmission> PlayAsync(string endpointId, ChannelLabScene scene, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        var format = device.AudioClient.MixFormat;
        byte[] original = DualSenseChannelFixtures.Build(scene);
        byte[] data = EncodeSource(original, format);
        var report = new EndpointEmission(device.ID, device.FriendlyName, "Explicit Shared WASAPI synthetic source (not reference)",
            DualSenseChannelFixtures.Sha256(original), DualSenseChannelFixtures.Sha256(data), original.Length / 8,
            format.BitsPerSample, device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
        if (report.EndpointMuted || report.EndpointVolume == 0)
            throw new InvalidOperationException("Test source endpoint is muted/zero-volume; no automatic volume change.");
        using var memory = new MemoryStream(data, writable: false);
        using var source = new RawSourceWaveStream(memory, format);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        output.Init(source);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception != null) completed.TrySetException(args.Exception);
            else completed.TrySetResult();
        };
        try
        {
            output.Play();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            return report;
        }
        finally { output.Stop(); }
    }
}
