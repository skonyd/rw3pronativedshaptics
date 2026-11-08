using System;

namespace RW3ProNativeDSHaptics;

public enum NativeDemo
{
    Hits, Sweep, PingPong, Rain, Directions8,
    Footsteps, Heartbeat, EngineRev, Explosion, MachineGun,
    BowDraw, Thunder, Gravel, SwordClash, FireCrackle,
    Purr, BouncingBall, Rocket, Typewriter, Rollercoaster, Bubbles, Drums, Electric
}

/// <summary>
/// Synthetic DualSense-style rear-channel (3/4) PCM used to feel the v2 Native pipeline
/// without a game. Every demo runs through exactly the same extractor and engine as live
/// capture. Test data only — never presented as game-authored haptics.
/// The Wolverine has two actuators (left/right palm): left/right is real panning, while
/// front/back in <see cref="NativeDemo.Directions8"/> is encoded with frequency and rhythm.
/// </summary>
public static class NativeDemoSignals
{
    public const int SampleRate = 48000;

    // ── 8 directions: order, timing, encoding ──
    public const int DirectionStepMs = 600, DirectionOnMs = 480;
    private static readonly string[] DirectionNames =
        { "Ön", "Ön-Sağ", "Sağ", "Arka-Sağ", "Arka", "Arka-Sol", "Sol", "Ön-Sol" };
    // x: -1 left .. +1 right; y: +1 front .. -1 back
    private static readonly (double X, double Y)[] DirectionVectors =
    {
        (0, 1), (0.71, 0.71), (1, 0), (0.71, -0.71), (0, -1), (-0.71, -0.71), (-1, 0), (-0.71, 0.71)
    };

    public static readonly NativeDemo[] All = (NativeDemo[])Enum.GetValues(typeof(NativeDemo));

    public static int DurationMs(NativeDemo demo) => demo switch
    {
        NativeDemo.Sweep => 4000,
        NativeDemo.EngineRev => 4000,
        NativeDemo.Directions8 => DirectionStepMs * 8,
        NativeDemo.Thunder => 3500,
        NativeDemo.Rocket => 4500,
        NativeDemo.Rollercoaster => 5000,
        NativeDemo.Drums => 4000,
        NativeDemo.Purr => 3600,
        _ => 3000
    };

    public static string Title(NativeDemo demo) => demo switch
    {
        NativeDemo.Hits => "Vuruşlar",
        NativeDemo.Sweep => "30→400 Hz",
        NativeDemo.PingPong => "Sol ↔ Sağ",
        NativeDemo.Rain => "Yağmur",
        NativeDemo.Directions8 => "8 yön",
        NativeDemo.Footsteps => "Ayak sesi",
        NativeDemo.Heartbeat => "Kalp atışı",
        NativeDemo.EngineRev => "Motor devri",
        NativeDemo.Explosion => "Patlama",
        NativeDemo.MachineGun => "Makineli",
        NativeDemo.BowDraw => "Yay çekme",
        NativeDemo.Thunder => "Gök gürültüsü",
        NativeDemo.Gravel => "Çakıl dokusu",
        NativeDemo.SwordClash => "Kılıç",
        NativeDemo.FireCrackle => "Ateş çıtırtısı",
        NativeDemo.Purr => "Kedi mırlaması",
        NativeDemo.BouncingBall => "Zıplayan top",
        NativeDemo.Rocket => "Roket kalkışı",
        NativeDemo.Typewriter => "Daktilo",
        NativeDemo.Rollercoaster => "Lunapark treni",
        NativeDemo.Bubbles => "Baloncuklar",
        NativeDemo.Drums => "Davul solosu",
        _ => "Elektrik akımı"
    };

    /// <summary>Live caption while a demo plays (direction name for the 8-direction test).</summary>
    public static string Caption(NativeDemo demo, int elapsedMs)
    {
        if (demo != NativeDemo.Directions8) return Title(demo) + " çalıyor…";
        int step = Math.Clamp(elapsedMs / DirectionStepMs, 0, 7);
        return "8 yön • " + (step + 1) + "/8 → " + DirectionNames[step];
    }

    /// <summary>Fills one block of interleaved 4ch PCM16 (channels 2/3 = left/right).</summary>
    public static void Fill(NativeDemo demo, long startSample, Span<byte> block, ref uint rng)
    {
        int frames = block.Length / 8;
        block.Clear();
        for (int i = 0; i < frames; i++)
        {
            long n = startSample + i;
            double t = n / (double)SampleRate;
            (double l, double r) = Sample(demo, t);
            short ls = (short)Math.Round(Math.Clamp(l, -1, 1) * 32767);
            short rs = (short)Math.Round(Math.Clamp(r, -1, 1) * 32767);
            block[i * 8 + 4] = (byte)ls; block[i * 8 + 5] = (byte)(ls >> 8);
            block[i * 8 + 6] = (byte)rs; block[i * 8 + 7] = (byte)(rs >> 8);
        }
    }

    /// <summary>One stereo sample (left, right) of a demo at time t seconds.</summary>
    public static (double L, double R) Sample(NativeDemo demo, double t) => demo switch
    {
        NativeDemo.Hits => Hits(t),
        NativeDemo.Sweep => Sweep(t, DurationMs(demo) / 1000.0),
        NativeDemo.PingPong => PingPong(t),
        NativeDemo.Rain => Rain(t),
        NativeDemo.Directions8 => Directions8(t),
        NativeDemo.Footsteps => Footsteps(t),
        NativeDemo.Heartbeat => Heartbeat(t),
        NativeDemo.EngineRev => EngineRev(t, DurationMs(demo) / 1000.0),
        NativeDemo.Explosion => Explosion(t),
        NativeDemo.MachineGun => MachineGun(t),
        NativeDemo.BowDraw => BowDraw(t),
        NativeDemo.Thunder => Thunder(t),
        NativeDemo.Gravel => Gravel(t),
        NativeDemo.SwordClash => SwordClash(t),
        NativeDemo.FireCrackle => FireCrackle(t),
        NativeDemo.Purr => Purr(t),
        NativeDemo.BouncingBall => BouncingBall(t),
        NativeDemo.Rocket => Rocket(t),
        NativeDemo.Typewriter => Typewriter(t),
        NativeDemo.Rollercoaster => Rollercoaster(t),
        NativeDemo.Bubbles => Bubbles(t),
        NativeDemo.Drums => Drums(t),
        _ => Electric(t)
    };

    // ── helpers ──
    private static double Sin(double hz, double t) => Math.Sin(2 * Math.PI * hz * t);
    private static double Decay(double local, double tau) => local < 0 ? 0 : Math.Exp(-local / tau);
    /// <summary>Deterministic 0..1 hash noise.</summary>
    private static double Hash(long k)
    {
        ulong h = (ulong)k * 0x9E3779B97F4A7C15UL;
        h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27;
        return (h >> 11) / (double)(1UL << 53);
    }
    /// <summary>Equal-power pan, x = -1 left .. +1 right.</summary>
    private static (double, double) Pan(double v, double x)
    {
        double a = (Math.Clamp(x, -1, 1) + 1) * Math.PI / 4;
        return (v * Math.Cos(a), v * Math.Sin(a));
    }

    // Gunshot-like: sharp onset, fast decay, alternating sides every 300 ms.
    private static (double, double) Hits(double t)
    {
        double period = 0.300, local = t % period;
        bool left = ((int)(t / period) % 2) == 0;
        double v = Decay(local, 0.035) * 0.95 * Sin(110, local);
        return left ? (v, 0) : (0, v);
    }

    // Logarithmic sweep 30 → 400 Hz on both sides at a constant level.
    private static (double, double) Sweep(double t, double duration)
    {
        double k = Math.Log(400.0 / 30.0) / duration;
        double v = 0.6 * Math.Sin(2 * Math.PI * 30 * (Math.Exp(k * t) - 1) / k);
        return (v, v);
    }

    // 80 Hz moving between the hands every 250 ms with soft edges.
    private static (double, double) PingPong(double t)
    {
        double local = t % 0.5;
        double pan = local < 0.25 ? 0 : 1;
        double edge = Math.Min(1, Math.Min(local % 0.25, 0.25 - local % 0.25) / 0.02);
        double v = 0.7 * edge * Sin(80, t);
        return (v * (1 - pan), v * pan);
    }

    // Short 220 Hz ticks at random times and sides.
    private static (double, double) Rain(double t)
    {
        long slot = (long)(t / 0.045);
        double local = t - slot * 0.045;
        double v = (local < 0.012 ? Decay(local, 0.004) : 0) * (0.35 + 0.4 * Hash(slot)) * Sin(220, local);
        return Hash(slot + 7777) < 0.5 ? (v, 0) : (0, v);
    }

    // 8 directions: lateral = equal-power pan; front = light 190 Hz taps (12/s), back = deep 45 Hz.
    private static (double, double) Directions8(double t)
    {
        int step = (int)(t * 1000 / DirectionStepMs);
        if (step > 7) return (0, 0);
        double local = t - step * DirectionStepMs / 1000.0;
        if (local > DirectionOnMs / 1000.0) return (0, 0);
        var (x, y) = DirectionVectors[step];
        double edge = Math.Min(1, Math.Min(local, DirectionOnMs / 1000.0 - local) / 0.025);
        double front = Math.Max(0, y), back = Math.Max(0, -y), center = 1 - front - back;
        double tap = 0.5 + 0.5 * Math.Sign(Math.Sin(2 * Math.PI * 12 * local));   // 12 Hz gate
        double v = front * 0.75 * tap * Sin(190, local)
                 + back * 0.9 * Sin(45, local)
                 + center * 0.8 * Sin(80, local);
        return Pan(edge * v, x);
    }

    // Footsteps every 0.45 s alternating feet: 70 Hz heel thump + gravel crunch.
    private static (double, double) Footsteps(double t)
    {
        long step = (long)(t / 0.45);
        double local = t - step * 0.45;
        double thump = Decay(local, 0.040) * 0.68 * Sin(70, local);
        long grain = (long)(local / 0.004);
        double crunch = local < 0.09 ? Decay(local, 0.030) * 0.28 * (Hash(step * 1000 + grain) - 0.5) * 2 * Sin(230, local) : 0;
        double v = thump + crunch;
        return step % 2 == 0 ? (v, 0.25 * v) : (0.25 * v, v);
    }

    // Lub-dub every 0.85 s, deep 55 Hz, both hands.
    private static (double, double) Heartbeat(double t)
    {
        double local = t % 0.85;
        double v = Decay(local, 0.06) * 0.9 * Sin(55, local) + Decay(local - 0.18, 0.06) * 0.6 * Sin(55, local - 0.18);
        return (v, v);
    }

    // Engine from idle (35 Hz) to high revs (140 Hz) with firing-pulse modulation.
    private static (double, double) EngineRev(double t, double duration)
    {
        double u = Math.Clamp(t / duration, 0, 1);
        double f = 35 + 105 * u * u;
        double phase = 2 * Math.PI * (35 * t + 105 * duration * Math.Pow(u, 3) / 3);
        double firing = 0.65 + 0.35 * Math.Sin(phase / 2);
        double v = (0.35 + 0.45 * u) * firing * Math.Sin(phase);
        _ = f;
        return (v, v);
    }

    // Big hit at 0.3 s then a 40 Hz rumble decaying over ~2 s.
    private static (double, double) Explosion(double t)
    {
        double local = t - 0.3;
        if (local < 0) return (0, 0);
        double hit = Decay(local, 0.05) * 0.95 * Sin(90, local);
        double rumble = Decay(local, 0.8) * 0.7 * Sin(40, local) * (0.7 + 0.3 * Hash((long)(local / 0.03)));
        double v = Math.Clamp(hit + rumble, -1, 1);
        return (v, v * 0.9);
    }

    // Shots every 70 ms for 2.2 s, mostly in the right hand.
    private static (double, double) MachineGun(double t)
    {
        if (t > 2.2) return (0, 0);
        double local = t % 0.07;
        double v = Decay(local, 0.015) * 0.9 * Sin(140, local);
        return (0.3 * v, v);
    }

    // Tension rises 0–1.8 s (30→90 Hz, creaking texture), sharp snap on release.
    private static (double, double) BowDraw(double t)
    {
        if (t < 1.8)
        {
            double u = t / 1.8;
            double creak = 0.7 + 0.3 * Hash((long)(t / 0.012));
            double v = (0.1 + 0.5 * u) * creak * Math.Sin(2 * Math.PI * (30 * t + 60 * t * u / 2));
            return (v, 0.6 * v);
        }
        double local = t - 1.8;
        double snap = Decay(local, 0.025) * 1.0 * Sin(160, local);
        return (snap, snap * 0.8);
    }

    // Crack at 0.6 s, then rolling 35–45 Hz rumble panning slowly left → right.
    private static (double, double) Thunder(double t)
    {
        double crack = Decay(t - 0.6, 0.03) * (t >= 0.6 ? 0.9 * Sin(120, t - 0.6) : 0);
        double swell = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.9 * t + 2 * Hash((long)(t / 0.25)));
        double roll = (t >= 0.5 ? Math.Min(1, (t - 0.5) / 0.3) * Decay(t - 1.2, 2.0) : 0) * 0.6 * swell * Sin(38 + 6 * Math.Sin(t), t);
        return Pan(Math.Clamp(crack + roll, -1, 1), -0.8 + 1.6 * Math.Clamp(t / 3.5, 0, 1));
    }

    // Dense random grains every ~8 ms, independent per hand.
    private static (double, double) Gravel(double t)
    {
        long g = (long)(t / 0.008);
        double local = t - g * 0.008;
        double l = Decay(local, 0.0025) * (0.2 + 0.35 * Hash(g)) * Sin(180 + 80 * Hash(g + 11), local);
        double r = Decay(local, 0.0025) * (0.2 + 0.35 * Hash(g + 5000)) * Sin(180 + 80 * Hash(g + 5011), local);
        return (l, r);
    }

    // Three clashes: sharp 250 Hz hit + 320 Hz metallic ring, alternating emphasis.
    private static (double, double) SwordClash(double t)
    {
        double[] at = { 0.3, 1.2, 2.1 };
        double l = 0, r = 0;
        for (int i = 0; i < at.Length; i++)
        {
            double local = t - at[i];
            if (local < 0) continue;
            double v = Decay(local, 0.008) * 1.0 * Sin(250, local) + Decay(local, 0.3) * 0.35 * Sin(320, local);
            if (i % 2 == 0) { l += v; r += 0.5 * v; } else { r += v; l += 0.5 * v; }
        }
        return (Math.Clamp(l, -1, 1), Math.Clamp(r, -1, 1));
    }

    // Sparse crackles (≈25 % of 10 ms slots) over a faint 40 Hz hum.
    private static (double, double) FireCrackle(double t)
    {
        long s = (long)(t / 0.010);
        double local = t - s * 0.010;
        double hum = 0.08 * Sin(40, t);
        double crackle = Hash(s) < 0.25 ? Decay(local, 0.002 + 0.003 * Hash(s + 3)) * (0.3 + 0.6 * Hash(s + 9)) * Sin(200 + 100 * Hash(s + 17), local) : 0;
        var (l, r) = Pan(crackle, Hash(s + 29) * 2 - 1);
        return (l + hum, r + hum);
    }
    // ── fun scenes ──

    // Purring cat: 35 Hz rumble breathing in/out (1.2 s cycle) with a soft 25 Hz flutter.
    private static (double, double) Purr(double t)
    {
        double breath = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 1.2);
        double flutter = 0.75 + 0.25 * Math.Sin(2 * Math.PI * 25 * t);
        double fade = Math.Min(1, Math.Min(t, 3.6 - t) / 0.2);
        double v = fade * (0.25 + 0.4 * breath) * flutter * Sin(35, t);
        return (v, v);
    }

    // Ball bouncing ever faster and softer, drifting left -> right.
    private static (double, double) BouncingBall(double t)
    {
        double at = 0.05, gap = 0.6, amp = 0.95;
        double l = 0, r = 0;
        for (int k = 0; k < 14 && at < 3.0; k++)
        {
            double local = t - at;
            if (local >= 0 && local < 0.15)
            {
                double v = Decay(local, 0.03) * amp * Sin(90, local);
                var (pl, pr) = Pan(v, -0.8 + 1.6 * (at / 3.0));
                l += pl; r += pr;
            }
            at += gap; gap *= 0.75; amp *= 0.82;
        }
        return (Math.Clamp(l, -1, 1), Math.Clamp(r, -1, 1));
    }

    // Ignition rumble, then lift-off: full shaking that fades and rises in pitch as it climbs.
    private static (double, double) Rocket(double t)
    {
        double grain = 0.7 + 0.3 * Hash((long)(t / 0.008));
        if (t < 0.8)
        {
            double v = (t / 0.8) * 0.45 * grain * Sin(32 + 10 * t, t);
            return (v, v);
        }
        double u = (t - 0.8) / 3.7;
        double env = Math.Max(0, 1 - u) * (u < 0.05 ? u / 0.05 : 1);
        double f = 40 + 60 * u;
        double v2 = env * grain * (0.75 * Math.Sin(2 * Math.PI * f * t) + 0.2 * Sin(110, t));
        return (Math.Clamp(v2, -1, 1), Math.Clamp(v2 * 0.95, -1, 1));
    }

    // Keystrokes ~9/s alternating hands; every 1.5 s a stronger carriage-return thunk.
    private static (double, double) Typewriter(double t)
    {
        long key = (long)(t / 0.11);
        double local = t - key * 0.11 - 0.02 * Hash(key);
        double click = local >= 0 ? Decay(local, 0.004) * (0.45 + 0.2 * Hash(key + 1)) * Sin(260, local) : 0;
        double ret = 0;
        double lr = t % 1.5;
        if (lr < 0.2) ret = Decay(lr - 0.02, 0.05) * (lr >= 0.02 ? 0.85 * Sin(70, lr) : 0);
        bool left = key % 2 == 0;
        double l = (left ? click : 0.2 * click) + ret, r = (left ? 0.2 * click : click) + ret;
        return (Math.Clamp(l, -1, 1), Math.Clamp(r, -1, 1));
    }

    // Lift-hill chain clicks, the big drop (rising rush), then left/right turns.
    private static (double, double) Rollercoaster(double t)
    {
        if (t < 1.8)
        {
            double local = t % 0.12;
            double v = Decay(local, 0.006) * 0.35 * Sin(150, local);
            return (v, v);
        }
        if (t < 2.8)
        {
            double u = (t - 1.8) / 1.0;
            double v = (0.3 + 0.65 * u) * (0.75 + 0.25 * Hash((long)(t / 0.01))) * Sin(70 - 30 * u, t);
            return (v, v);
        }
        double turn = Math.Sin(2 * Math.PI * (t - 2.8) / 1.1);
        double rush = Math.Max(0, 1 - (t - 2.8) / 2.2) * 0.8 * (0.8 + 0.2 * Hash((long)(t / 0.01))) * Sin(45, t);
        return Pan(rush, turn);
    }

    // Bubbles rising: short 120 -> 320 Hz chirps at random moments and sides.
    private static (double, double) Bubbles(double t)
    {
        long slot = (long)(t / 0.14);
        double start = slot * 0.14 + 0.08 * Hash(slot);
        double local = t - start;
        if (local < 0 || local > 0.06) return (0, 0);
        double phase = 2 * Math.PI * (120 * local + (200 / 0.06) * local * local / 2);
        double v = Math.Sin(Math.PI * local / 0.06) * (0.3 + 0.3 * Hash(slot + 5)) * Math.Sin(phase);
        return Pan(v, Hash(slot + 9) * 2 - 1);
    }

    // 120 BPM groove: kick (both), snare (right), hi-hat (left), tom fill panning L -> R.
    private static (double, double) Drums(double t)
    {
        double eighth = 0.25;
        long step = (long)(t / eighth);
        double local = t - step * eighth;
        int pos = (int)(step % 8);
        bool fill = t >= 3.0;
        double l = 0, r = 0;
        if (!fill)
        {
            if (pos == 0 || pos == 4 || pos == 5) { double k = Decay(local, 0.07) * 0.9 * Sin(55, local); l += k; r += k; }
            if (pos == 2 || pos == 6) { double sn = Decay(local, 0.04) * (0.7 * Sin(180, local) + 0.3 * (Hash((long)(t / 0.002)) - 0.5)); r += sn; l += 0.3 * sn; }
            l += Decay(local, 0.003) * 0.3 * Sin(300, local);
        }
        else
        {
            double sixteenth = 0.125;
            long hit = (long)((t - 3.0) / sixteenth);
            double hl = t - 3.0 - hit * sixteenth;
            double tom = Decay(hl, 0.05) * 0.85 * Sin(110 - 8 * hit, hl);
            var (pl, pr) = Pan(tom, -1 + 2 * Math.Min(1, (t - 3.0) / 1.0));
            l += pl; r += pr;
        }
        return (Math.Clamp(l, -1, 1), Math.Clamp(r, -1, 1));
    }

    // Electric current: 120 Hz buzz that surges, with sparks jumping between hands.
    private static (double, double) Electric(double t)
    {
        double surge = 0.35 + 0.3 * Math.Abs(Math.Sin(2 * Math.PI * 1.3 * t)) + 0.15 * Hash((long)(t / 0.02));
        double buzz = surge * Math.Sign(Sin(120, t)) * 0.6;   // square-ish buzz
        long s = (long)(t / 0.09);
        double ls = t - s * 0.09;
        double spark = Hash(s) < 0.4 ? Decay(ls, 0.003) * 0.9 * Sin(280, ls) : 0;
        double side = Hash(s + 3) < 0.5 ? -1 : 1;
        var (sl, sr) = Pan(spark, side);
        double l = 0.6 * buzz + sl, r = 0.6 * buzz + sr;
        return (Math.Clamp(l, -1, 1), Math.Clamp(r, -1, 1));
    }
}
