namespace RW3TestRange;

/// <summary>Which output paths the game uses (chosen on the start screen, several at once).</summary>
[Flags]
public enum Out
{
    None = 0,
    Music = 1, Sfx = 2,
    /// <summary>XInput rumble (two body motors): most PC games.</summary>
    XInput = 4,
    /// <summary>GameInput rumble incl. the trigger (impulse) motors: GDK / Game Pass games.</summary>
    GameInput = 8,
    /// <summary>DualSense HID report 0x02: adaptive triggers, lightbar, player LEDs; compatible rumble when HD haptics are off.</summary>
    DsHid = 16,
    /// <summary>DualSense HD haptics: PCM on channels 3/4 of the pad's audio endpoint.</summary>
    DsHaptics = 32,
    /// <summary>Sound effects also on the DualSense's own speaker (channels 1/2).</summary>
    DsSpeaker = 64,
    /// <summary>DualShock 4 HID report 0x05: rumble + lightbar.</summary>
    Ds4 = 128
}

/// <summary>
/// Sends the game's feedback to every chosen output, like a game that supports several pad types, and logs each
/// send. Rumble is kept as short decaying envelopes and written at most ~60 times a second.
/// </summary>
public sealed class FeedbackHub : IDisposable
{
    private sealed class Env { public float Low, High, Lt, Rt; public double Left, Total; }
    private readonly List<Env> _envs = new();
    private float _sLow, _sHigh, _sLt, _sRt;
    private double _sinceSend;
    private bool _sentNonZero;

    public Out Enabled;
    public readonly List<XInputPad> XPads = new();
    public GameInputHub? GameInput;
    public readonly List<DualSenseHid> DualSenses = new();
    public readonly List<DualShock4Hid> Ds4s = new();
    /// <summary>"Bağlı kol (RW3'süz)": send only to real Sony pads, never to RW3's virtual ones.</summary>
    public bool RealOnly;
    private IEnumerable<DualSenseHid> DsTargets => RealOnly ? DualSenses.Where(d => !d.Virtual) : DualSenses;
    private IEnumerable<DualShock4Hid> Ds4Targets => RealOnly ? Ds4s.Where(d => !d.Virtual) : Ds4s;
    /// <summary>Quick-table rows: the game's rumble / trigger feedback on or off (game setting).</summary>
    public bool RumbleOn = true, TriggersOn = true;
    /// <summary>Game settings: HD haptic level (0..2).</summary>
    public float HapticGain = 1;
    /// <summary>Game settings: effect volume (0..1).</summary>
    public float SfxGain = 1;
    public SpeakerOutput? Speakers;
    public HapticOutput? HapticOut;
    public float RumbleScale = 1;

    public bool Has(Out o) => (Enabled & o) != 0;

    /// <summary>Body rumble (and, with <paramref name="lt"/>/<paramref name="rt"/>, trigger motors) for <paramref name="seconds"/>, fading out.</summary>
    public void Rumble(float low, float high, double seconds, float lt = 0, float rt = 0, string? why = null)
    {
        lock (_envs) _envs.Add(new Env { Low = low, High = high, Lt = lt, Rt = rt, Left = seconds, Total = seconds });
        SessionLog.Current?.Count("rumble events");
        if (why != null) SessionLog.Current?.Event("rumble", new { why, low, high, lt, rt, seconds, to = Targets(Out.XInput | Out.GameInput | Out.DsHid) });
    }

    private double _keepAlive;

    /// <summary>Sends the strongest active envelope; call every frame.</summary>
    public void Update(double dt)
    {
        // DualSense: haptic mode follows the outputs, and the state is resent every 100 ms like DSX / games do
        // (a single report did not wake the actuators on real hardware).
        bool hd = Has(Out.DsHaptics);
        foreach (var ds in DualSenses) ds.AudioHaptics = hd;
        if (Has(Out.DsHid) && (_keepAlive += dt) >= .1) { _keepAlive = 0; foreach (var ds in DsTargets) if (ds.Send()) SessionLog.Current?.Count("dualsense reports (keep-alive)"); }
        float low = 0, high = 0, lt = 0, rt = 0;
        lock (_envs)
            for (int i = _envs.Count - 1; i >= 0; i--)
            {
                var e = _envs[i];
                e.Left -= dt;
                if (e.Left <= 0) { _envs.RemoveAt(i); continue; }
                float k = (float)(e.Left / e.Total);
                low = Math.Max(low, e.Low * k); high = Math.Max(high, e.High * k); lt = Math.Max(lt, e.Lt * k); rt = Math.Max(rt, e.Rt * k);
            }
        low *= RumbleScale; high *= RumbleScale; lt *= RumbleScale; rt *= RumbleScale;
        if (!RumbleOn) low = high = 0;
        if (!TriggersOn) lt = rt = 0;
        _sinceSend += dt;
        bool zero = low + high + lt + rt < .01f;
        bool changed = Math.Abs(low - _sLow) > .03f || Math.Abs(high - _sHigh) > .03f || Math.Abs(lt - _sLt) > .03f || Math.Abs(rt - _sRt) > .03f;
        if (zero && !_sentNonZero) return;
        if (!changed && !(!zero && _sinceSend > .1) && !(zero && _sentNonZero)) return;
        if (_sinceSend < 1 / 60.0 && !zero) return;
        _sLow = low; _sHigh = high; _sLt = lt; _sRt = rt; _sinceSend = 0; _sentNonZero = !zero;
        if (Has(Out.XInput)) foreach (var p in XPads) if (p.Rumble(low, high)) SessionLog.Current?.Count("xinput writes");
        if (Has(Out.GameInput) && GameInput != null)
            foreach (var d in GameInput.Devices)
            {
                // GameInput also lists Sony pads; they get their feedback from their own HID report, not twice.
                if (d.Sony && (Has(Out.DsHid) || Has(Out.Ds4))) continue;
                if (d.Rumble(low, high, lt, rt)) SessionLog.Current?.Count("gameinput writes");
            }
        if (Has(Out.DsHaptics) && HapticOut != null && RumbleOn) { HapticOut.RumbleLow = low; HapticOut.RumbleHigh = high; }
        else if (Has(Out.DsHid) && RumbleOn)
            foreach (var ds in DsTargets)
            {
                ds.RumbleLeft = (byte)(low * 255); ds.RumbleRight = (byte)(high * 255);
                if (ds.Send()) SessionLog.Current?.Count("dualsense reports (rumble)");
            }
        if (Has(Out.Ds4))
            foreach (var p in Ds4Targets)
            {
                p.RumbleLeft = (byte)(low * 255); p.RumbleRight = (byte)(high * 255);
                if (p.Send()) SessionLog.Current?.Count("dualshock4 reports (rumble)");
            }
    }

    public void Adaptive(TriggerFx left, TriggerFx right, string why)
    {
        if (!Has(Out.DsHid)) return;
        if (!TriggersOn) { left = TriggerFx.Off; right = TriggerFx.Off; }
        foreach (var ds in DsTargets)
        {
            if (ds.LeftTrigger.Mode == left.Mode && ds.LeftTrigger.P.SequenceEqual(left.P) && ds.RightTrigger.Mode == right.Mode && ds.RightTrigger.P.SequenceEqual(right.P)) continue;
            ds.LeftTrigger = left; ds.RightTrigger = right;
            if (ds.Send()) SessionLog.Current?.Count("dualsense reports (triggers)");
            SessionLog.Current?.Event("adaptive-trigger", new { why, left = left.ToString(), right = right.ToString(), pad = ds.Name });
        }
    }

    public void Light(byte r, byte g, byte b, byte playerLeds, string why)
    {
        if (Has(Out.Ds4))
            foreach (var p in Ds4Targets)
                if (p.Red != r || p.Green != g || p.Blue != b) { p.Red = r; p.Green = g; p.Blue = b; if (p.Send()) SessionLog.Current?.Count("dualshock4 reports (light)"); }
        if (!Has(Out.DsHid)) return;
        foreach (var ds in DsTargets)
        {
            if (ds.Red == r && ds.Green == g && ds.Blue == b && ds.PlayerLeds == playerLeds) continue;
            ds.Red = r; ds.Green = g; ds.Blue = b; ds.PlayerLeds = playerLeds;
            if (ds.Send()) SessionLog.Current?.Count("dualsense reports (light)");
            SessionLog.Current?.Event("light", new { why, r, g, b, playerLeds });
        }
    }

    public void MicLed(bool on)
    {
        if (!Has(Out.DsHid)) return;
        foreach (var ds in DsTargets) { ds.MicLed = (byte)(on ? 1 : 0); ds.Send(); }
    }

    /// <summary>DualSense HD haptics (stereo: left/right actuator).</summary>
    public void Haptic(float[] stereo, string why, float gain = 1)
    {
        if (!Has(Out.DsHaptics) || HapticOut == null) return;
        HapticOut.Haptics.Play(stereo, gain * HapticGain);
        SessionLog.Current?.Count("hd haptic clips");
        SessionLog.Current?.Event("hd-haptic", new { why, seconds = Math.Round(stereo.Length / 2.0 / Synth.Rate, 3), gain });
    }

    public void Sound(float[] stereo, string why, float gain = 1)
    {
        if (Has(Out.Sfx) && Speakers != null) { Speakers.Sfx.Play(stereo, gain * SfxGain * .7f); SessionLog.Current?.Count("sfx played"); }
        if (Has(Out.DsSpeaker) && HapticOut != null) HapticOut.PadSpeaker.Play(stereo, gain * .7f);
        SessionLog.Current?.Event("sfx", new { why });
    }

    public string Targets(Out mask)
    {
        var parts = new List<string>();
        if ((mask & Out.XInput) != 0 && Has(Out.XInput)) parts.Add($"XInput×{XPads.Count}");
        if ((mask & Out.GameInput) != 0 && Has(Out.GameInput)) parts.Add($"GameInput×{GameInput?.Devices.Count ?? 0}");
        if ((mask & Out.DsHid) != 0 && Has(Out.DsHid)) parts.Add($"DualSense×{DualSenses.Count}");
        if ((mask & Out.DsHid) != 0 && Has(Out.Ds4)) parts.Add($"DS4×{Ds4s.Count}");
        return string.Join(",", parts);
    }

    public void StopAll()
    {
        lock (_envs) _envs.Clear();
        foreach (var p in XPads) p.Rumble(0, 0);
        if (GameInput != null) foreach (var d in GameInput.Devices) d.Rumble(0, 0, 0, 0);
        foreach (var ds in DualSenses) { ds.RumbleLeft = ds.RumbleRight = 0; ds.LeftTrigger = ds.RightTrigger = TriggerFx.Off; ds.Send(); }
        foreach (var p in Ds4s) { p.RumbleLeft = p.RumbleRight = 0; p.Send(); }
        HapticOut?.Haptics.Clear();
        if (HapticOut != null) { HapticOut.RumbleLow = 0; HapticOut.RumbleHigh = 0; }
        _sentNonZero = false; _sLow = _sHigh = _sLt = _sRt = 0;
    }

    public void Dispose()
    {
        try { StopAll(); } catch { }
        foreach (var p in XPads) p.Dispose();
        foreach (var ds in DualSenses) ds.Dispose();
        foreach (var p in Ds4s) p.Dispose();
        GameInput?.Dispose();
        Speakers?.Dispose();
        HapticOut?.Dispose();
    }
}
