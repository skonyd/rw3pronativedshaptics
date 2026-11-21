namespace RW3TestRange;

/// <summary>Reads every pad the game can see and follows the one used last (like a game that accepts any pad).</summary>
public sealed class InputHub
{
    public readonly List<IPadSource> Sources = new();
    private readonly Dictionary<IPadSource, PadState> _last = new();
    public IPadSource? Active { get; private set; }
    public PadState State { get; private set; }
    public PadState Previous { get; private set; }

    public void Poll()
    {
        Previous = State;
        foreach (var s in Sources)
        {
            if (!s.TryRead(out var st)) continue;
            bool moved = _last.TryGetValue(s, out var old) && !old.SameAs(st);
            _last[s] = st;
            if (Active == null || moved && !ReferenceEquals(s, Active) && (st.Buttons != 0 || MathF.Abs(st.LX) + MathF.Abs(st.LY) + st.LT + st.RT > .3f))
            {
                if (!ReferenceEquals(Active, s)) SessionLog.Current?.Event("input-source", new { source = s.Name });
                Active = s;
            }
        }
        State = Active != null && _last.TryGetValue(Active, out var a) ? a : default;
    }

    public bool Pressed(Btn b) => State.Down(b) && !Previous.Down(b);
    public bool Released(Btn b) => !State.Down(b) && Previous.Down(b);
    public bool Sony => Active?.Sony ?? false;
}
