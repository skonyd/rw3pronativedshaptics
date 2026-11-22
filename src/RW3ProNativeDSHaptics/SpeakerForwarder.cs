using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// The virtual DualSense's own speaker (channels 1/2 of its 4-channel audio endpoint) on the PC's default playback
/// device. A game that sees a DualSense plays some sounds on the controller speaker; the Wolverine has none, and RW3
/// dropped those channels, so those sounds were simply missing (2026-11-23, The Last of Us Part II).
/// The output opens on the first non-silent block and stays open while the bridge runs; it is never opened on a
/// DualSense endpoint (that would feed the virtual pad its own sound). Blocks that do not fit the buffer are dropped
/// (the game's clock and the PC's never match exactly): a dropped block is a click, a growing buffer is a delay.
/// </summary>
public sealed class SpeakerForwarder : IDisposable
{
    private readonly object _gate = new();
    private BufferedWaveProvider? _buffer;
    private WasapiOut? _output;
    private bool _failed;
    private byte[] _stereo = Array.Empty<byte>();
    public float PeakLeft, PeakRight;
    public string Note { get; private set; } = "kapalı";

    /// <summary>Takes one interleaved PCM16 4-channel block (speaker L, speaker R, haptic L, haptic R, 48 kHz).</summary>
    public void Push(ReadOnlySpan<byte> pcm4)
    {
        int frames = pcm4.Length / 8;
        if (frames == 0) return;
        if (_stereo.Length < frames * 4) _stereo = new byte[frames * 4];
        float pl = 0, pr = 0;
        for (int f = 0; f < frames; f++)
        {
            int s = f * 8, d = f * 4;
            _stereo[d] = pcm4[s]; _stereo[d + 1] = pcm4[s + 1]; _stereo[d + 2] = pcm4[s + 2]; _stereo[d + 3] = pcm4[s + 3];
            pl = Math.Max(pl, Math.Abs((short)(pcm4[s] | (pcm4[s + 1] << 8)) / 32768f));
            pr = Math.Max(pr, Math.Abs((short)(pcm4[s + 2] | (pcm4[s + 3] << 8)) / 32768f));
        }
        PeakLeft = Math.Max(PeakLeft, pl); PeakRight = Math.Max(PeakRight, pr);
        lock (_gate)
        {
            if (_buffer == null)
            {
                if (_failed || Math.Max(pl, pr) < 0.002f) return;   // nothing to play yet
                Open();
                if (_buffer == null) return;
            }
            if (_buffer.BufferedDuration.TotalMilliseconds < 150) _buffer.AddSamples(_stereo, 0, frames * 4);
        }
    }

    private void Open()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
            {
                _failed = true; Note = "varsayılan çıkış DualSense, aktarılmıyor";
                DiagnosticsService.Info("Virtual DualSense speaker: default output is a DualSense, not forwarded.");
                return;
            }
            _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true };
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            _output.Init(_buffer);
            _output.Play();
            Note = "→ " + device.FriendlyName;
            DiagnosticsService.Info("Virtual DualSense speaker: forwarded to " + device.FriendlyName + ".");
        }
        catch (Exception ex)
        {
            _failed = true; _buffer = null;
            try { _output?.Dispose(); } catch { }
            _output = null;
            Note = "açılamadı: " + ex.Message;
            DiagnosticsService.Error("Virtual DualSense speaker forward", ex);
        }
    }

    /// <summary>"speaker 0.42/0.40 → TV" for the bridge's log line, then reset the peaks.</summary>
    public string Take()
    {
        string s = FormattableString.Invariant($"; speaker {PeakLeft:F2}/{PeakRight:F2} {Note}");
        PeakLeft = PeakRight = 0;
        return s;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null; _buffer = null; _failed = false; Note = "kapalı";
        }
    }
}
