namespace RW3TestRange;

/// <summary>Reads every pad the game can see and follows the one used last (like a game that accepts any pad).</summary>
public sealed class InputHub
{
    public readonly List<IPadSource> Sources = new();
    /// <summary>"Bağlı kol (RW3'süz)": ignore RW3's virtual Sony pads as input.</summary>
    public bool RealOnly;
    private readonly Dictionary<IPadSource, PadState> _last = new();
    public IPadSource? Active { get; private set; }
    public PadState State { get; private set; }
    public PadState Previous { get; private set; }

    public void Poll()
    {
        Previous = State;
        foreach (var s in Sources)
        {
            if (RealOnly && (s is DualSenseHid { Virtual: true } || s is DualShock4Hid { Virtual: true })) continue;
            if (!s.TryRead(out var st)) { Stat(s).Fails++; continue; }
            bool moved = _last.TryGetValue(s, out var old) && !old.SameAs(st);
            var stat = Stat(s);
            stat.Reads++;
            if (moved) stat.Changes++;
            stat.Buttons |= st.Buttons;
            stat.Stick = MathF.Max(stat.Stick, MathF.Max(MathF.Max(MathF.Abs(st.LX), MathF.Abs(st.LY)), MathF.Max(MathF.Abs(st.RX), MathF.Abs(st.RY))));
            stat.Lx = MathF.Max(stat.Lx, MathF.Abs(st.LX)); stat.Ly = MathF.Max(stat.Ly, MathF.Abs(st.LY));
            stat.Rx = MathF.Max(stat.Rx, MathF.Abs(st.RX)); stat.Ry = MathF.Max(stat.Ry, MathF.Abs(st.RY));
            if (st.Touching) stat.Touch++;
            if (st.HasMotion) stat.Gyro = MathF.Max(stat.Gyro, MathF.Max(MathF.Abs(st.GyroX), MathF.Abs(st.GyroY)));
            stat.Trigger = MathF.Max(stat.Trigger, MathF.Max(st.LT, st.RT));
            stat.Both = MathF.Max(stat.Both, MathF.Min(st.LT, st.RT));   // LT + RT together (a game's two-trigger move)
            _last[s] = st;
            if (Active == null || moved && !ReferenceEquals(s, Active) && (st.Buttons != 0 || MathF.Abs(st.LX) + MathF.Abs(st.LY) + st.LT + st.RT > .3f))
            {
                if (!ReferenceEquals(Active, s)) SessionLog.Current?.Event("input-source", new { source = s.Name });
                Active = s;
            }
        }
        State = Active != null && _last.TryGetValue(Active, out var a) ? a : default;
    }

    // Per source since the last TakeStats: does each path (XInput slot, GameInput device, HID) see the sticks, D-pad
    // and triggers, or only buttons? (2026-11-22: Banishers took "press any button" from RW3's virtual Xbox pad but
    // nothing moved; this game reads the same GameInput path while in front.)
    // 2026-11-23: per axis, touch and gyro too: the aim reads LX/RX, and a reported touch or gyro moves it as well
    private sealed class SourceStat { public int Reads, Fails, Changes, Touch; public Btn Buttons; public float Stick, Trigger, Both, Lx, Ly, Rx, Ry, Gyro; }
    private readonly Dictionary<IPadSource, SourceStat> _stats = new();
    private SourceStat Stat(IPadSource s) => _stats.TryGetValue(s, out var v) ? v : _stats[s] = new SourceStat();

    /// <summary>One entry per source for the window, then reset.</summary>
    public List<object> TakeStats()
    {
        var list = new List<object>();
        foreach (var (s, v) in _stats)
            list.Add(new { source = s.Name, active = ReferenceEquals(s, Active), v.Reads, v.Fails, v.Changes, buttons = v.Buttons.ToString(),
                stick = MathF.Round(v.Stick, 2), trigger = MathF.Round(v.Trigger, 2), both = MathF.Round(v.Both, 2),
                lx = MathF.Round(v.Lx, 2), ly = MathF.Round(v.Ly, 2), rx = MathF.Round(v.Rx, 2), ry = MathF.Round(v.Ry, 2), touch = v.Touch, gyro = MathF.Round(v.Gyro, 1) });
        _stats.Clear();
        return list;
    }

    public bool Pressed(Btn b) => State.Down(b) && !Previous.Down(b);
    public bool Released(Btn b) => !State.Down(b) && Previous.Down(b);
    public bool Sony => Active?.Sony ?? false;
}
