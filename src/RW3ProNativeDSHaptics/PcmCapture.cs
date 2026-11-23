using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>Diagnostics: records what the game sends to the virtual DualSense's audio device (4 channels, PCM16:
/// speaker L/R, haptic L/R) and, at the same time, what Windows plays on the default output (the TV), when the user
/// asks for it (2026-11-23: TLOU2's haptics seemed to follow the dialogue, and the TV crackled only with the DualSense
/// output). Armed by the flag file <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\haptic-capture.flag</c>; writes
/// <c>haptic-capture.wav</c>, <c>tv-capture.wav</c> and <c>haptic-capture-blocks.txt</c> (arrival time of every
/// virtual-audio block, ms from the start) next to it and deletes the flag. Never runs otherwise.</summary>
public sealed class PcmCapture
{
    public const int Seconds = 60;
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics");
    public static string FlagFile => Path.Combine(Dir, "haptic-capture.flag");
    public static string WavFile => Path.Combine(Dir, "haptic-capture.wav");
    public static string TvFile => Path.Combine(Dir, "tv-capture.wav");
    public static string BlocksFile => Path.Combine(Dir, "haptic-capture-blocks.txt");

    private readonly int _channels, _rate;
    private long _nextCheck, _start;
    private MemoryStream? _data;
    private List<string>? _blocks;
    private WasapiCapture? _tv;
    private WaveFileWriter? _tvWriter;
    private readonly object _tvGate = new();

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
            _blocks = new List<string>(Seconds * 110);
            _start = Stopwatch.GetTimestamp();
            StartTv();
            DiagnosticsService.Info($"Haptic capture: recording {Seconds} s of the virtual DualSense audio ({_channels} ch) and the default output.");
        }
        _blocks!.Add(FormattableString.Invariant($"{(Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency:F2} {pcm.Length / (_channels * 2)}"));
        _data.Write(pcm);
        if (_data.Length < (long)_rate * _channels * 2 * Seconds) return;
        var done = _data; var blocks = _blocks; _data = null; _blocks = null;
        StopTv();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                File.WriteAllBytes(WavFile, Wav(done.ToArray(), _channels, _rate));
                File.WriteAllLines(BlocksFile, blocks);
                DiagnosticsService.Info("Haptic capture: saved " + WavFile + ", " + TvFile + ".");
            }
            catch (Exception ex) { DiagnosticsService.Info("Haptic capture: not saved: " + ex.Message); }
        });
    }

    private void StartTv()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var capture = new ConfigurableWasapiLoopbackCapture(device, 10);
            var writer = new WaveFileWriter(TvFile, capture.WaveFormat);
            long first = 0;
            capture.DataAvailable += (_, e) =>
            {
                lock (_tvGate)
                {
                    if (first == 0) { first = Stopwatch.GetTimestamp(); DiagnosticsService.Info(FormattableString.Invariant($"Haptic capture: TV loopback starts at {(first - _start) * 1000.0 / Stopwatch.Frequency:F1} ms ({device.FriendlyName}, {capture.WaveFormat}).")); }
                    _tvWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
            lock (_tvGate) { _tv = capture; _tvWriter = writer; }
            capture.StartRecording();
        }
        catch (Exception ex) { DiagnosticsService.Info("Haptic capture: TV loopback not started: " + ex.Message); StopTv(); }
    }

    private void StopTv()
    {
        WasapiCapture? tv; WaveFileWriter? w;
        lock (_tvGate) { tv = _tv; w = _tvWriter; _tv = null; _tvWriter = null; }
        try { tv?.StopRecording(); } catch { }
        try { w?.Dispose(); } catch { }
        try { tv?.Dispose(); } catch { }
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
