using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>Which "Sesten titreşim" check to play.</summary>
public enum AudioTestKind { Glide, PingPong, Directions8, FrontBack }

/// <summary>
/// "Sesten titreşim" checks: real stereo audio is played on the default playback device and the
/// audio-haptics route under test turns it into vibration.
///   Glide:       80 Hz tone (4 Hz pulsing) glides left → right (0–3 s), then right → left (3–6 s).
///   PingPong:    exactly the Native v2 "Sol ↔ Sağ" demo (80 Hz, 250 ms per side, 3 s), played at
///                1/PreGain so it reaches the engine at the demo's level.
///   Directions8: the 8-direction scene of <see cref="NativeDemoSignals"/> as audio.
///   FrontBack:   front (sharp 190 Hz taps) and back (deep smooth 45 Hz) alternate, both hands.
/// The controller only has a left and a right actuator: left/right is position, front/back is
/// texture (high + tapping = front, deep + smooth = back), as in the 8-direction demo.
/// </summary>
public static class AudioPanTest
{
    public const int DurationMs = 6000;
    // 80 Hz like the Native v2 ping-pong demo. 55-60 Hz sits next to the actuators' 50 Hz
    // resonance and shakes the whole shell, so it was felt in both hands (user, 2026-11-07).
    public const double ToneHz = 80, PulseHz = 4, Level = 0.28;   // unity input; Wolverine output gain is separate

    public const int PingPongHopMs = 250;   // the demo switches sides every 250 ms
    public const int FrontBackSteps = 8, FrontBackStepMs = 750, FrontBackOnMs = 600;
    public const double FrontHz = 190, BackHz = 45;
    private const double FrontBoost = 1.5;   // Authored demo balance, not microphone-calibrated frequency compensation.

    public static int DurationOf(AudioTestKind kind) => kind switch
    {
        AudioTestKind.PingPong => NativeDemoSignals.DurationMs(NativeDemo.PingPong),
        AudioTestKind.Directions8 => NativeDemoSignals.DurationMs(NativeDemo.Directions8),
        AudioTestKind.FrontBack => FrontBackSteps * FrontBackStepMs,
        _ => DurationMs
    };

    public static string Title(AudioTestKind kind) => kind switch
    {
        AudioTestKind.PingPong => "Ses testi • en sol ↔ en sağ",
        AudioTestKind.Directions8 => "Ses testi • 8 yön",
        AudioTestKind.FrontBack => "Ses testi • ön ↔ arka",
        _ => "Ses testi • sol → sağ → sol"
    };

    /// <summary>Pan position at time t: -1 = left, +1 = right.</summary>
    public static double PanAt(double t) => t < 3.0 ? -1 + 2 * (t / 3.0) : 1 - 2 * ((t - 3.0) / 3.0);

    public static string Caption(int elapsedMs) => Caption(AudioTestKind.Glide, elapsedMs);

    public static string Caption(AudioTestKind kind, int elapsedMs)
    {
        switch (kind)
        {
            case AudioTestKind.PingPong:
            {
                int hops = DurationOf(kind) / PingPongHopMs;
                int hop = Math.Clamp(elapsedMs / PingPongHopMs, 0, hops - 1);
                return Title(kind) + " • " + (hop + 1) + "/" + hops + " → " + (hop % 2 == 0 ? "◀ SOL" : "SAĞ ▶");
            }
            case AudioTestKind.Directions8:
                return "Ses testi • " + NativeDemoSignals.Caption(NativeDemo.Directions8, elapsedMs) +
                       "  (ön = keskin tıkırtı, arka = derin uğultu)";
            case AudioTestKind.FrontBack:
            {
                int step = Math.Clamp(elapsedMs / FrontBackStepMs, 0, FrontBackSteps - 1);
                bool on = elapsedMs - step * FrontBackStepMs < FrontBackOnMs;
                return Title(kind) + " • " + (step + 1) + "/" + FrontBackSteps + " → " +
                       (on ? (step % 2 == 0 ? "▲ ÖN (keskin, hızlı tıkırtı)" : "▼ ARKA (derin, yumuşak uğultu)") : "…");
            }
            default:
            {
                double p = PanAt(elapsedMs / 1000.0);
                string where = p < -0.6 ? "SOL" : p < -0.2 ? "sol-orta" : p <= 0.2 ? "ORTA" : p <= 0.6 ? "sağ-orta" : "SAĞ";
                string dir = elapsedMs < 3000 ? "sol → sağ" : "sağ → sol";
                return "Ses testi • " + dir + " • şu an: " + where;
            }
        }
    }

    /// <summary>One stereo sample (left, right) of the glide test at time t seconds.</summary>
    public static (float L, float R) Sample(double t) => Sample(AudioTestKind.Glide, t);

    public static (float L, float R) Sample(AudioTestKind kind, double t)
    {
        switch (kind)
        {
            case AudioTestKind.PingPong:
            {
                if (t < 0 || t * 1000 >= DurationOf(kind)) return (0, 0);
                var (l, r) = NativeDemoSignals.Sample(NativeDemo.PingPong, t);
                return ((float)(l / SensaAudioSink.PreGain), (float)(r / SensaAudioSink.PreGain));
            }
            case AudioTestKind.Directions8:
            {
                // Demo levels go up to ~0.9; scale to the audio test level.
                var (l, r) = NativeDemoSignals.Sample(NativeDemo.Directions8, t);
                const double scale = 0.4;
                return ((float)(l * scale), (float)(r * scale));
            }
            case AudioTestKind.FrontBack:
            {
                int step = (int)(t * 1000 / FrontBackStepMs);
                if (step < 0 || step >= FrontBackSteps) return (0, 0);
                double local = t - step * FrontBackStepMs / 1000.0;
                double env = Burst(local, FrontBackOnMs / 1000.0);
                double v;
                if (step % 2 == 0)
                {
                    double tap = 0.5 + 0.5 * Math.Sign(Math.Sin(2 * Math.PI * 12 * local));   // 12 Hz gate
                    v = Level * FrontBoost * env * tap * Math.Sin(2 * Math.PI * FrontHz * local);
                }
                else v = Level * 1.1 * env * Math.Sin(2 * Math.PI * BackHz * local);
                return ((float)v, (float)v);
            }
            default:
            {
                double fade = Math.Min(1, Math.Min(t, DurationMs / 1000.0 - t) / 0.15);
                double pulse = 0.7 + 0.3 * Math.Sin(2 * Math.PI * PulseHz * t);
                double v = Level * fade * pulse * Math.Sin(2 * Math.PI * ToneHz * t);
                double a = (PanAt(t) + 1) * Math.PI / 4;
                return ((float)(v * Math.Cos(a)), (float)(v * Math.Sin(a)));
            }
        }
    }

    /// <summary>Burst envelope with 20 ms ramps (no clicks), zero outside 0..onSeconds.</summary>
    private static double Burst(double local, double onSeconds) =>
        local < 0 || local > onSeconds ? 0 : Math.Min(1, Math.Min(local, onSeconds - local) / 0.020);

    public static Task PlayAsync(IProgress<int>? progress, CancellationToken token, string? deviceId = null) =>
        PlayAsync(AudioTestKind.Glide, progress, token, deviceId);

    /// <summary>Plays the test on the default playback device (Shared, its own mix rate).</summary>
    public static async Task PlayAsync(AudioTestKind kind, IProgress<int>? progress, CancellationToken token, string? deviceId = null)
    {
        int durationMs = DurationOf(kind);
        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId != null ? enumerator.GetDevice(deviceId)
                                      : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int sr = device.AudioClient.MixFormat.SampleRate;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sr, 2);
        int frames = sr * durationMs / 1000;
        var pcm = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            var (l, r) = Sample(kind, i / (double)sr);
            pcm[2 * i] = l; pcm[2 * i + 1] = r;
        }
        var bytes = new byte[pcm.Length * 4];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var provider = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromMilliseconds(durationMs + 1000) };
        provider.AddSamples(bytes, 0, bytes.Length);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        output.Init(provider);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        output.Play();
        try
        {
            while (sw.ElapsedMilliseconds < durationMs + 200)
            {
                progress?.Report((int)Math.Min(durationMs - 1, sw.ElapsedMilliseconds));
                await Task.Delay(50, token);
            }
        }
        finally { output.Stop(); }
    }
}
