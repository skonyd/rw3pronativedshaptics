using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

public static class DemoRouting
{
    /// <summary>Plays the haptic channels into a 4-channel DualSense endpoint (the virtual one) in shared mode.</summary>
    public static async Task PlayToEndpointAsync(string endpointId, float[] l, float[] r, IProgress<int>? elapsedMs, CancellationToken token)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDevice(endpointId);
        var fmt = dev.AudioClient.MixFormat;
        if (fmt.Channels != 4) throw new InvalidOperationException("DualSense uç noktası 4 kanallı değil: " + fmt);
        byte[] pcm = GameSignalSimulator.Interleave(l, r, fmt);
        using var ms = new MemoryStream(pcm, false);
        using var src = new RawSourceWaveStream(ms, fmt);
        using var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
        output.Init(src);
        var clock = Stopwatch.StartNew();
        output.Play();
        double seconds = l.Length / 48000.0;
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds + 0.2)
            {
                token.ThrowIfCancellationRequested();
                elapsedMs?.Report((int)clock.ElapsedMilliseconds);
                await Task.Delay(50, token);
            }
        }
        finally { output.Stop(); }
    }

    /// <summary>Drives the Xbox body motors from the haptic channels in real time (5 ms blocks, XInput).</summary>
    public static async Task PlayToXboxBodyAsync(float[] l, float[] r, int preferredSlot, int outputLevelPercent,
        IProgress<int>? elapsedMs, CancellationToken token)
    {
        var sink = new XboxAudioSink { PreferredIndex = preferredSlot, OutputLevelPercent = outputLevelPercent };
        if (!sink.HasGamepad) throw new InvalidOperationException("Xbox kolu XInput'ta bulunamadı (Ayarlar'daki yuvayı kontrol et).");
        var clock = Stopwatch.StartNew();
        int sent = 0, total = l.Length;
        var buf = new float[2 * 4800];
        try
        {
            while (sent < total)
            {
                token.ThrowIfCancellationRequested();
                int due = Math.Min(total, (int)(clock.Elapsed.TotalSeconds * 48000));
                while (sent < due)
                {
                    int n = Math.Min(due - sent, buf.Length / 2);
                    for (int i = 0; i < n; i++) { buf[2 * i] = l[sent + i]; buf[2 * i + 1] = r[sent + i]; }
                    sink.Accept(buf.AsSpan(0, 2 * n));
                    sent += n;
                }
                elapsedMs?.Report((int)clock.ElapsedMilliseconds);
                await Task.Delay(5, token);
            }
        }
        finally { sink.Silence(); }
    }
}
