using System;
using System.IO;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>Diagnostics: records what the game sends to the virtual DualSense's audio device (4 channels, PCM16:
/// speaker L/R, haptic L/R) for a few seconds when the user asks for it, so the channels can be told apart offline
/// (2026-11-23: TLOU2's haptics followed the dialogue, and the palms crackled). Armed by the flag file
/// <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\haptic-capture.flag</c>; writes <c>haptic-capture.wav</c> next to it and
/// deletes the flag. Never runs otherwise.</summary>
public sealed class PcmCapture
{
    public const int Seconds = 15;
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics");
    public static string FlagFile => Path.Combine(Dir, "haptic-capture.flag");
    public static string WavFile => Path.Combine(Dir, "haptic-capture.wav");

    private readonly int _channels, _rate;
    private long _nextCheck;
    private MemoryStream? _data;

    public PcmCapture(int channels = 4, int rate = 48000) { _channels = channels; _rate = rate; }

    /// <summary>Called with every block (interleaved PCM16). Cheap when not armed: one file check a second.</summary>
    public void Note(ReadOnlySpan<byte> pcm)
    {
        if (_data == null)
        {
            long now = Environment.TickCount64;
            if (now < _nextCheck) return;
            _nextCheck = now + 1000;
            try { if (!File.Exists(FlagFile)) return; File.Delete(FlagFile); } catch { return; }
            _data = new MemoryStream(_rate * _channels * 2 * Seconds);
            DiagnosticsService.Info($"Haptic capture: recording {Seconds} s of the virtual DualSense audio ({_channels} ch).");
        }
        _data.Write(pcm);
        if (_data.Length < (long)_rate * _channels * 2 * Seconds) return;
        var done = _data; _data = null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                File.WriteAllBytes(WavFile, Wav(done.ToArray(), _channels, _rate));
                DiagnosticsService.Info("Haptic capture: saved " + WavFile + ".");
            }
            catch (Exception ex) { DiagnosticsService.Info("Haptic capture: not saved: " + ex.Message); }
        });
    }

    public static byte[] Wav(byte[] pcm, int channels, int rate)
    {
        var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
            w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        }
        return ms.ToArray();
    }
}
