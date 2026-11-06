using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// ORIGINAL synthetic reference scenes. They are NOT Sony game-authored haptics,
/// not a substitute for a physical A/B experiment, and not a Razer WAV sink.
/// Four-channel signed PCM16/48k: ch0/1 silent, ch2 left and ch3 right haptic.
/// </summary>
public enum HapticBehavior
{
    Rain, SandFootsteps, GravelFootsteps, GrassFootsteps, Pistol,
    AutomaticRifle, EngineIdle, Acceleration, Explosion, Thunder
}

public sealed record BehavioralScene(HapticBehavior Id, string Name, string Feel, int DurationMs);

public static class BehavioralHapticFixtures
{
    public const int SampleRate = 48000;
    public const int Channels = 4;
    public const int BytesPerFrame = 8;
    public const float MaximumPeak = 0.22f;

    public static IReadOnlyList<BehavioralScene> All { get; } = Array.AsReadOnly(new[]
    {
        new BehavioralScene(HapticBehavior.Rain,"01 • Yağmur damlaları","Farklı taraflarda mikro darbeler, hafif ve seyrek dokular",3000),
        new BehavioralScene(HapticBehavior.SandFootsteps,"02 • Kumda yürüyüş","Yumuşak ayak basışları, alçak frekanslı sürtünme",3200),
        new BehavioralScene(HapticBehavior.GravelFootsteps,"03 • Çakılda yürüyüş","Daha keskin çakıl ezilme ve taneli dokular",3200),
        new BehavioralScene(HapticBehavior.GrassFootsteps,"04 • Çimende yürüyüş","Hafif fırçalanma ve düşük darbeli yaprak sürtünmesi",3000),
        new BehavioralScene(HapticBehavior.Pistol,"05 • Tabanca tekli atış","Üç ayrı kısa geri tepme darbesi, sağ taraf ağırlıklı",2800),
        new BehavioralScene(HapticBehavior.AutomaticRifle,"06 • Otomatik tüfek","Kısa seri, hızlı tekrarlayan tok darbeler",2800),
        new BehavioralScene(HapticBehavior.EngineIdle,"07 • Motor rölantisi","Düşük frekanslı sürekli titreşim ve hafif dalgalanma",3000),
        new BehavioralScene(HapticBehavior.Acceleration,"08 • Araç hızlanma","Kademeli yükselen frekans ve genlik",3200),
        new BehavioralScene(HapticBehavior.Explosion,"09 • Patlama ve şok dalgası","Başlangıçta güçlü darbe, ardından azalan yayılım",3000),
        new BehavioralScene(HapticBehavior.Thunder,"10 • Gök gürültüsü","Uzakta başlayıp yaklaşan düzensiz alçak frekanslı dalga",3200)
    });

    public static byte[] Generate(HapticBehavior scene)
    {
        if (!Enum.IsDefined(scene)) throw new ArgumentOutOfRangeException(nameof(scene));
        int durationMs = All[(int)scene].DurationMs;
        int frames = SampleRate * durationMs / 1000;
        byte[] result = new byte[frames * BytesPerFrame];
        double previousLeftNoise = 0, previousRightNoise = 0;
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)SampleRate;
            // Fixed seed per scene and index: repeatable, auditably identical A/B source.
            double nL = Noise(i, (int)scene * 73 + 11);
            double nR = Noise(i, (int)scene * 113 + 29);
            previousLeftNoise = previousLeftNoise * 0.94 + nL * 0.06;
            previousRightNoise = previousRightNoise * 0.94 + nR * 0.06;
            double l = 0, r = 0;
            double baseTone = Math.Sin(2 * Math.PI * (84 * t + 0.8 * Math.Sin(2 * Math.PI * 2 * t)));
            switch (scene)
            {
                case HapticBehavior.Rain:
                    l = RainDrops(t, 0, nL, previousLeftNoise);
                    r = RainDrops(t, 0.13, nR, previousRightNoise);
                    break;
                case HapticBehavior.SandFootsteps:
                    l = Footstep(t, 0, 0.56, 0.28, previousLeftNoise, 0.53);
                    r = Footstep(t, 0.56, 0.56, 0.28, previousRightNoise, 0.53);
                    break;
                case HapticBehavior.GravelFootsteps:
                    l = Footstep(t, 0, 0.49, 0.27, nL * 0.48 + previousLeftNoise, 0.69);
                    r = Footstep(t, 0.49, 0.49, 0.27, nR * 0.48 + previousRightNoise, 0.69);
                    break;
                case HapticBehavior.GrassFootsteps:
                    l = Footstep(t, 0, 0.52, 0.35, previousLeftNoise, 0.32);
                    r = Footstep(t, 0.52, 0.52, 0.35, previousRightNoise, 0.32);
                    break;
                case HapticBehavior.Pistol:
                    l = ImpulseTrain(t, 0.45, 0.82, 3, 0.060, 0.78, 88, nL);
                    r = ImpulseTrain(t, 0.45, 0.82, 3, 0.060, 1.00, 118, nR);
                    break;
                case HapticBehavior.AutomaticRifle:
                    l = ImpulseTrain(t, 0.35, 0.11, 15, 0.055, 0.68, 94, nL);
                    r = ImpulseTrain(t, 0.35, 0.11, 15, 0.055, 1.00, 145, nR);
                    break;
                case HapticBehavior.EngineIdle:
                    l = (Math.Sin(2 * Math.PI * 56 * t) * 0.62 +
                         Math.Sin(2 * Math.PI * 112 * t) * 0.2) *
                        (0.56 + 0.12 * Math.Sin(2 * Math.PI * 4 * t));
                    r = (Math.Sin(2 * Math.PI * 57 * t + 0.3) * 0.60 +
                         Math.Sin(2 * Math.PI * 114 * t) * 0.20) *
                        (0.55 + 0.12 * Math.Sin(2 * Math.PI * 4 * t));
                    break;
                case HapticBehavior.Acceleration:
                    double p = t / (durationMs / 1000.0);
                    phase += 2 * Math.PI * (45 + p * 110) / SampleRate;
                    l = Math.Sin(phase) * (0.22 + p * 0.68);
                    r = Math.Sin(phase + 0.22) * (0.22 + p * 0.70);
                    break;
                case HapticBehavior.Explosion:
                    double boom = Decay(t - 0.38, 0.54);
                    double secondary = Decay(t - 0.76, 0.35) * 0.22;
                    l = (boom + secondary) * (0.70 * baseTone + 0.3 * previousLeftNoise);
                    r = (Decay(t - 0.40, 0.55) + secondary) *
                        (0.71 * Math.Sin(2 * Math.PI * 76 * t) + 0.29 * previousRightNoise);
                    break;
                case HapticBehavior.Thunder:
                    double swell = Math.Pow(Math.Max(0, Math.Sin(Math.PI * Math.Clamp((t - 0.35) / 2.5, 0, 1))), 1.6);
                    l = swell * (0.70 * Math.Sin(2 * Math.PI * 53 * t) +
                                 0.30 * previousLeftNoise);
                    r = Math.Max(0, Math.Sin(Math.PI * Math.Clamp((t - 0.49) / 2.35, 0, 1))) *
                        (0.72 * Math.Sin(2 * Math.PI * 56 * t) + 0.28 * previousRightNoise);
                    break;
            }
            double fade = Math.Min(1, Math.Min(i, frames - 1 - i) / 960.0);
            // Bound each output at 22% PCM full scale; never send any speaker audio.
            Write(result, i * BytesPerFrame + 4, l * MaximumPeak * fade);
            Write(result, i * BytesPerFrame + 6, r * MaximumPeak * fade);
        }
        return result;
    }

    // The DualSense comparison receives Generate() output directly: no software
    // volume, gain, stereo mixing, filtering or remapping. The Windows audio
    // stack may still process shared-mode output; actual games own their stream.

    public static (float Left, float Right) RmsWindow(
        ReadOnlySpan<byte> samples, int firstFrame, int frames)
    {
        if (samples.Length % BytesPerFrame != 0 || firstFrame < 0 || frames < 0 ||
            firstFrame + frames > samples.Length / BytesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(firstFrame));
        if (frames == 0) return (0, 0);
        double l = 0, r = 0;
        // Bounded work: 20 ms => 960 samples, sample every fourth frame.
        int stride = Math.Max(1, (frames + 255) / 256);
        int n = 0;
        for (int j = 0; j < frames; j += stride)
        {
            int o = (firstFrame + j) * BytesPerFrame;
            double vl = BitConverter.ToInt16(samples.Slice(o + 4, 2)) / 32768.0;
            double vr = BitConverter.ToInt16(samples.Slice(o + 6, 2)) / 32768.0;
            l += vl * vl;
            r += vr * vr;
            n++;
        }
        return ((float)Math.Sqrt(l / n), (float)Math.Sqrt(r / n));
    }

    private static double Noise(int sample, int seed)
    {
        uint v = unchecked((uint)(sample * 1664525 + seed * 1013904223));
        v ^= v >> 15; v *= 2246822519u; v ^= v >> 13;
        return (v / (double)uint.MaxValue) * 2 - 1;
    }

    private static double RainDrops(double t, double phase, double noise, double smooth)
    {
        double pos = (t + phase) % 0.137;
        double spike = pos < 0.033 ? Math.Exp(-pos * 105) : 0;
        double weight = 0.32 + 0.45 * Math.Abs(Math.Sin((t + phase) * 13));
        return weight * (0.72 * spike * Math.Sin(2 * Math.PI * 165 * t) +
                         0.18 * smooth + 0.10 * noise);
    }

    private static double Footstep(double t, double offset, double interval, double length,
        double texture, double volume)
    {
        double p = ((t - offset) % (2 * interval) + 2 * interval) % (2 * interval);
        if (p >= length) return 0;
        double envelope = Math.Pow(Math.Sin(Math.PI * p / length), 1.5);
        return volume * envelope * (0.64 * Math.Sin(2 * Math.PI * 86 * t) + 0.36 * texture);
    }

    private static double ImpulseTrain(double t, double start, double interval, int total,
        double length, double strength, double hz, double noise)
    {
        double since = t - start;
        if (since < 0) return 0;
        int k = (int)(since / interval);
        if (k >= total) return 0;
        double p = since - k * interval;
        if (p >= length) return 0;
        double envelope = Math.Exp(-p * 57);
        return strength * envelope *
            (0.83 * Math.Sin(2 * Math.PI * hz * p) + 0.17 * noise);
    }

    private static double Decay(double t, double decaySeconds) =>
        t < 0 ? 0 : Math.Exp(-t / decaySeconds) * Math.Min(1, t / 0.018);

    private static void Write(byte[] destination, int offset, double value) =>
        WritePcm(destination, offset, (short)Math.Round(
            Math.Clamp(value, -MaximumPeak, MaximumPeak) * short.MaxValue));
    private static void WritePcm(byte[] destination, int offset, short value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
    }
}
