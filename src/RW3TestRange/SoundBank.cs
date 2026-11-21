using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RW3TestRange;

/// <summary>
/// Recorded sounds (CC0, see Sounds/CREDITS.md) loaded from the Sounds folder next to the exe, as stereo float at
/// <see cref="Synth.Rate"/>. A key may have several files (key.wav, key_2.wav, ...): one is picked at random so repeats
/// do not sound identical. A missing key returns null and the caller falls back to its synthesised sound.
/// </summary>
public static class SoundBank
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Sounds");
    private static readonly Dictionary<string, float[][]> Cache = new();

    /// <summary>All variants of <paramref name="key"/>, or null when there is no file for it.</summary>
    public static float[][]? Variants(string key)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit.Length > 0 ? hit : null;
            var list = new List<float[]>();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var f in Directory.GetFiles(Folder, "*.wav").Where(f => NameOf(f) == key).OrderBy(f => f))
                        try { list.Add(Load(f)); } catch (Exception ex) { SessionLog.Current?.Event("note", new { text = "sound load failed: " + Path.GetFileName(f) + " " + ex.Message }); }
            }
            catch { }
            Cache[key] = list.ToArray();
            return list.Count > 0 ? list.ToArray() : null;
        }
    }

    /// <summary>"pistol_2.wav" → "pistol".</summary>
    private static string NameOf(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        int u = n.LastIndexOf('_');
        return u > 0 && int.TryParse(n[(u + 1)..], out _) ? n[..u] : n;
    }

    public static bool Has(string key) => Variants(key) != null;

    /// <summary>A random variant, panned (-1 left .. +1 right, equal power) and scaled; null when missing.</summary>
    public static float[]? Get(string key, float pan = 0, float gain = 1, double pitch = 1)
    {
        var v = Variants(key);
        if (v == null) return null;
        var src = v[Random.Shared.Next(v.Length)];
        if (Math.Abs(pitch - 1) > .001) src = Repitch(src, pitch);
        double a = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        // equal power, normalised so centre = unity on both sides
        float gl = (float)(Math.Cos(a) * Math.Sqrt(2)) * gain, gr = (float)(Math.Sin(a) * Math.Sqrt(2)) * gain;
        var y = new float[src.Length];
        for (int i = 0; i + 1 < src.Length; i += 2) { y[i] = src[i] * gl; y[i + 1] = src[i + 1] * gr; }
        return y;
    }

    /// <summary>Linear-interpolated speed change (pitch and length together), for variety on one-off recordings.</summary>
    private static float[] Repitch(float[] s, double ratio)
    {
        int frames = s.Length / 2, outFrames = (int)(frames / ratio);
        var y = new float[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            double p = i * ratio; int k = (int)p; float t = (float)(p - k);
            if (k + 1 >= frames) break;
            y[2 * i] = s[2 * k] + (s[2 * k + 2] - s[2 * k]) * t;
            y[2 * i + 1] = s[2 * k + 1] + (s[2 * k + 3] - s[2 * k + 1]) * t;
        }
        return y;
    }

    /// <summary>Any PCM/float WAV → stereo float at Synth.Rate.</summary>
    public static float[] Load(string file)
    {
        using var reader = new AudioFileReader(file);
        ISampleProvider p = reader;
        if (p.WaveFormat.Channels == 1) p = new MonoToStereoSampleProvider(p);
        else if (p.WaveFormat.Channels > 2) throw new NotSupportedException(p.WaveFormat.Channels + " channels");
        if (p.WaveFormat.SampleRate != Synth.Rate) p = new WdlResamplingSampleProvider(p, Synth.Rate);
        var all = new List<float>(Synth.Rate * 4);
        var buf = new float[Synth.Rate];
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < n; i++) all.Add(buf[i]);
        return all.ToArray();
    }
}

/// <summary>Background music from Sounds/music*.wav, looped with a short crossfade; one track after the other.</summary>
public sealed class FileMusic : IMusicSource
{
    public float Volume { get; set; } = .5f;
    private readonly float[][] _tracks;
    private int _track, _pos;
    private const int Fade = Synth.Rate / 2;   // 0.5 s crossfade at the loop point (frames)

    private FileMusic(float[][] tracks) => _tracks = tracks;

    /// <summary>Null when the folder holds no music file.</summary>
    public static FileMusic? TryCreate()
    {
        var v = SoundBank.Variants("music");
        return v == null ? null : new FileMusic(v);
    }

    public string Name => $"dosya ({_tracks.Length} parça)";

    public void Read(float[] buffer, int offset, int frames)
    {
        float vol = Volume;
        for (int f = 0; f < frames; f++)
        {
            var t = _tracks[_track];
            int len = t.Length / 2;
            float l = t[2 * _pos], r = t[2 * _pos + 1];
            int left = len - _pos;
            if (left <= Fade && len > 2 * Fade)
            {
                // crossfade into the start of the next track (or this one again)
                var next = _tracks[(_track + 1) % _tracks.Length];
                int q = Fade - left;
                float g = q / (float)Fade;
                l = l * (1 - g) + next[2 * q] * g;
                r = r * (1 - g) + next[2 * q + 1] * g;
            }
            buffer[offset + 2 * f] += l * vol * .6f;   // mastered tracks: about as loud as the synth loop was
            buffer[offset + 2 * f + 1] += r * vol * .6f;
            if (++_pos >= len)
            {
                _track = (_track + 1) % _tracks.Length;
                _pos = len > 2 * Fade ? Fade : 0;   // the crossfade already played the first Fade frames
            }
        }
    }
}

/// <summary>What <see cref="SpeakerOutput"/> plays as music: recorded tracks, else the synthesised loop.</summary>
public interface IMusicSource
{
    float Volume { get; set; }
    void Read(float[] buffer, int offset, int frames);
}
