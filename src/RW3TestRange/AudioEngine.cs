using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3TestRange;

/// <summary>Plays stereo float buffers on top of each other (thread-safe).</summary>
public sealed class VoiceMixer
{
    private sealed class Voice { public required float[] Data; public int Pos; public float Gain; }
    private readonly List<Voice> _voices = new();
    private readonly object _gate = new();
    public long Started { get; private set; }

    public void Play(float[] stereo, float gain = 1)
    {
        lock (_gate) { _voices.Add(new Voice { Data = stereo, Gain = gain }); Started++; }
    }

    public void Clear() { lock (_gate) _voices.Clear(); }

    /// <summary>Adds the active voices into <paramref name="buffer"/> (stereo frames). Returns the peak written.</summary>
    public float Mix(float[] buffer, int offset, int frames)
    {
        float peak = 0;
        lock (_gate)
        {
            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                int n = Math.Min(frames * 2, voice.Data.Length - voice.Pos);
                for (int i = 0; i < n; i++)
                {
                    float s = voice.Data[voice.Pos + i] * voice.Gain;
                    buffer[offset + i] += s;
                    if (MathF.Abs(s) > peak) peak = MathF.Abs(s);
                }
                voice.Pos += n;
                if (voice.Pos >= voice.Data.Length) _voices.RemoveAt(v);
            }
        }
        return peak;
    }
}

/// <summary>Music + sound effects on the default speakers / headset (what RW3's audio-to-haptics listens to).</summary>
public sealed class SpeakerOutput : ISampleProvider, IDisposable
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Synth.Rate, 2);
    public readonly VoiceMixer Sfx = new();
    /// <summary>Recorded CC0 tracks from Sounds/ (music*.wav), else the synthesised loop.</summary>
    public readonly IMusicSource Music = (IMusicSource?)FileMusic.TryCreate() ?? new MusicGenerator();
    public volatile bool MusicOn, SfxOn;
    private WasapiOut? _out;
    public string Device { get; private set; } = "-";
    public string? Error { get; private set; }

    public bool Start()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Device = dev.FriendlyName;
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
            _out.Init(this);
            _out.Play();
            return true;
        }
        catch (Exception ex) { Error = ex.Message; return false; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Not Array.Clear: NAudio hands over a WaveBuffer, a byte[] seen as float[]. Array.Clear works on the real
        // element type and cleared only count BYTES (a quarter of the floats); the rest kept the previous block, the
        // music/sfx below were added (+=) on top of it, and the speakers played a growing hiss / buzz instead of the
        // game's sound (2026-11-22, loopback correlation with the expected mix 0.26 → see EXPERIMENTS).
        for (int i = 0; i < count; i++) buffer[offset + i] = 0;
        int frames = count / 2;
        if (MusicOn) Music.Read(buffer, offset, frames);
        if (SfxOn) Sfx.Mix(buffer, offset, frames); else Sfx.Mix(new float[count], 0, frames);
        for (int i = 0; i < count; i++) buffer[offset + i] = Dsp.Soft(buffer[offset + i]);   // soft limiter, no hard clipping
        return count;
    }

    public void Dispose() { try { _out?.Stop(); } catch { } _out?.Dispose(); }
}

/// <summary>
/// The DualSense's 4-channel audio endpoint, written the way PS5-port games do: channels 1/2 = the pad's speaker,
/// channels 3/4 = the left/right haptic actuators (HD haptics). With RW3's virtual DualSense this is how game haptics
/// reach the Wolverine's palms.
/// </summary>
public sealed class HapticOutput : IWaveProvider, IDisposable
{
    public readonly VoiceMixer Haptics = new();
    public readonly VoiceMixer PadSpeaker = new();
    /// <summary>Game rumble played as haptics (as PS5 games do): low (left, ~55 Hz) and high (right, ~150 Hz) 0..1.</summary>
    public volatile float RumbleLow, RumbleHigh;
    private double _rlPh, _rhPh; private float _rlAmp, _rhAmp;
    private WasapiOut? _out;
    private float[] _mix = Array.Empty<float>(), _spk = Array.Empty<float>();
    private bool _float;
    public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(Synth.Rate, 4);
    public string Device { get; private set; } = "-";
    public string? Error { get; private set; }
    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public long FramesWritten { get; private set; }

    private static readonly NAudio.CoreAudioApi.PropertyKey ContainerKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    /// <summary>DualSense 4-channel endpoints with their device container (the same physical / virtual pad as its HID).</summary>
    public static List<(string Id, string Name, Guid? Container)> Endpoints()
    {
        var list = new List<(string, string, Guid?)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d)
                    if ((d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase))
                        && d.AudioClient.MixFormat.Channels == 4)
                    {
                        Guid? container = null;
                        try { if (d.Properties.Contains(ContainerKey) && d.Properties[ContainerKey].Value is Guid g) container = g; } catch { }
                        list.Add((d.ID, d.FriendlyName, container));
                    }
        }
        catch { }
        return list;
    }

    public bool Start(string endpointId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(endpointId);
            Device = dev.FriendlyName;
            var fmt = dev.AudioClient.MixFormat;
            if (fmt.Channels != 4) { Error = "uç nokta 4 kanal değil: " + fmt; return false; }
            WaveFormat = fmt;
            _float = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                     (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            if (!_float && fmt.BitsPerSample != 16) { Error = "desteklenmeyen biçim: " + fmt; return false; }
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
            _out.Init(this);
            _out.Play();
            return true;
        }
        catch (Exception ex) { Error = ex.Message; return false; }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytes = WaveFormat.BitsPerSample / 8, frame = bytes * 4, frames = count / frame;
        if (_mix.Length < frames * 2) { _mix = new float[frames * 2]; _spk = new float[frames * 2]; }
        Array.Clear(_mix, 0, frames * 2); Array.Clear(_spk, 0, frames * 2);
        Haptics.Mix(_mix, 0, frames);
        float tl = RumbleLow, th = RumbleHigh;
        for (int f = 0; f < frames; f++)
        {
            _rlAmp += (tl - _rlAmp) * .004f; _rhAmp += (th - _rhAmp) * .004f;   // smooth, no clicks
            if (_rlAmp < 1e-4f && _rhAmp < 1e-4f) continue;
            _rlPh += 2 * Math.PI * 55 / Synth.Rate; _rhPh += 2 * Math.PI * 150 / Synth.Rate;
            float lo = (float)Math.Sin(_rlPh) * _rlAmp * .8f, hi = (float)Math.Sin(_rhPh) * _rhAmp * .6f;
            _mix[2 * f] += lo + hi * .3f; _mix[2 * f + 1] += hi + lo * .3f;
        }
        PadSpeaker.Mix(_spk, 0, frames);
        float pl = 0, pr = 0;
        for (int f = 0; f < frames; f++)
        {
            float s0 = Math.Clamp(_spk[2 * f], -1, 1), s1 = Math.Clamp(_spk[2 * f + 1], -1, 1);
            float h0 = Math.Clamp(_mix[2 * f], -1, 1), h1 = Math.Clamp(_mix[2 * f + 1], -1, 1);
            pl = Math.Max(pl, Math.Abs(h0)); pr = Math.Max(pr, Math.Abs(h1));
            int at = offset + f * frame;
            if (_float)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(at, 4), s0); BitConverter.TryWriteBytes(buffer.AsSpan(at + 4, 4), s1);
                BitConverter.TryWriteBytes(buffer.AsSpan(at + 8, 4), h0); BitConverter.TryWriteBytes(buffer.AsSpan(at + 12, 4), h1);
            }
            else
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(at, 2), (short)(s0 * 32767)); BitConverter.TryWriteBytes(buffer.AsSpan(at + 2, 2), (short)(s1 * 32767));
                BitConverter.TryWriteBytes(buffer.AsSpan(at + 4, 2), (short)(h0 * 32767)); BitConverter.TryWriteBytes(buffer.AsSpan(at + 6, 2), (short)(h1 * 32767));
            }
        }
        PeakL = pl; PeakR = pr; FramesWritten += frames;
        return frames * frame;
    }

    public void Dispose() { try { _out?.Stop(); } catch { } _out?.Dispose(); }
}
