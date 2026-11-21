using System.Runtime.InteropServices;

namespace RW3TestRange;

/// <summary>
/// GameInput (inbox GameInput.dll, API v0), the GDK / Game Pass path. Reads gamepads and drives rumble WITH the two
/// trigger (impulse) motors, which XInput cannot. Vtable: IGameInput 4 GetCurrentReading, 9 RegisterDeviceCallback,
/// 13 UnregisterCallback; IGameInputDevice 3 GetDeviceInfo, 10 SetRumbleState; IGameInputReading 22 GetGamepadState.
/// </summary>
public sealed class GameInputHub : IDisposable
{
    private const uint KindGamepad = 0x00040000;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr InfoFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadingFn(IntPtr self, uint kind, IntPtr device, out IntPtr reading);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeviceCallback(ulong token, IntPtr context, IntPtr device, ulong timestamp, uint current, uint previous);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterFn(IntPtr self, IntPtr device, uint kind, uint status, int enumeration, IntPtr context, DeviceCallback cb, out ulong token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool UnregisterFn(IntPtr self, ulong token, ulong timeout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool StateFn(IntPtr self, out GiPad state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RumbleFn(IntPtr self, ref GiRumble r);
    [StructLayout(LayoutKind.Sequential)] private struct GiPad { public uint Buttons; public float LT, RT, LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct GiRumble { public float Low, High, LeftTrigger, RightTrigger; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("GameInput.dll", ExactSpelling = true)] private static extern int GameInputCreate(out IntPtr gi);
    private static T Slot<T>(IntPtr o, int i) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o), i * IntPtr.Size));

    private IntPtr _gi;
    private readonly List<Device> _devices = new();
    public string? Error { get; private set; }
    public IReadOnlyList<Device> Devices => _devices;

    public sealed class Device : IPadSource
    {
        internal IntPtr Ptr;
        private readonly GameInputHub _hub;
        public ushort Vid { get; }
        public ushort Pid { get; }
        internal Device(GameInputHub hub, IntPtr ptr, ushort vid, ushort pid) { _hub = hub; Ptr = ptr; Vid = vid; Pid = pid; }
        public string Name => $"GameInput {Vid:X4}:{Pid:X4}";
        public string Kind => "gameinput";
        public bool Sony => Vid == 0x054C;
        public bool Connected => Ptr != IntPtr.Zero;
        public bool TryRead(out PadState s) => _hub.Read(this, out s);
        /// <summary>low/high = body motors, lt/rt = trigger (impulse) motors, 0..1.</summary>
        public bool Rumble(float low, float high, float lt, float rt) => _hub.SetRumble(this, low, high, lt, rt);
        public void Dispose() { }
    }

    public bool Open()
    {
        try
        {
            if (_gi == IntPtr.Zero) { int hr = GameInputCreate(out _gi); if (hr < 0) { Error = $"GameInputCreate 0x{hr:X8}"; _gi = IntPtr.Zero; return false; } }
            var found = new List<(IntPtr, ushort, ushort)>();
            DeviceCallback cb = (_, _, d, _, cur, _) =>
            {
                try
                {
                    if ((cur & 1) == 0) return;
                    IntPtr info = Slot<InfoFn>(d, 3)(d);
                    Slot<RefFn>(d, 1)(d);
                    lock (found) found.Add((d, (ushort)Marshal.ReadInt16(info, 4), (ushort)Marshal.ReadInt16(info, 6)));
                }
                catch { }
            };
            int r = Slot<RegisterFn>(_gi, 9)(_gi, IntPtr.Zero, KindGamepad, 1, 2, IntPtr.Zero, cb, out ulong token);
            if (r >= 0) Slot<UnregisterFn>(_gi, 13)(_gi, token, ulong.MaxValue);
            GC.KeepAlive(cb);
            foreach (var d in _devices) Release(d.Ptr);
            _devices.Clear();
            foreach (var (p, v, pid) in found) _devices.Add(new Device(this, p, v, pid));
            Error = r < 0 ? $"enumeration 0x{r:X8}" : null;
            return true;
        }
        catch (Exception ex) { Error = ex.Message; return false; }
    }

    private bool Read(Device d, out PadState s)
    {
        s = default;
        IntPtr reading = IntPtr.Zero;
        try
        {
            if (_gi == IntPtr.Zero || Slot<ReadingFn>(_gi, 4)(_gi, KindGamepad, d.Ptr, out reading) < 0 || reading == IntPtr.Zero) return false;
            if (!Slot<StateFn>(reading, 22)(reading, out var g)) return false;
            Btn b = Btn.None;
            void M(uint bit, Btn f) { if ((g.Buttons & bit) != 0) b |= f; }
            // GameInputGamepadButtons: Menu 1, View 2, A 4, B 8, X 16, Y 32, Up 64, Down 128, Left 256, Right 512, LB 1024, RB 2048, LS 4096, RS 8192
            M(1, Btn.Menu); M(2, Btn.View); M(4, Btn.A); M(8, Btn.B); M(16, Btn.X); M(32, Btn.Y); M(64, Btn.Up); M(128, Btn.Down);
            M(256, Btn.Left); M(512, Btn.Right); M(1024, Btn.LB); M(2048, Btn.RB); M(4096, Btn.LS); M(8192, Btn.RS);
            s.Buttons = b; s.LT = g.LT; s.RT = g.RT; s.LX = g.LX; s.LY = g.LY; s.RX = g.RX; s.RY = g.RY;
            s.DeriveTriggerButtons();
            return true;
        }
        catch { return false; }
        finally { if (reading != IntPtr.Zero) Release(reading); }
    }

    private bool SetRumble(Device d, float low, float high, float lt, float rt)
    {
        try
        {
            var r = new GiRumble { Low = Math.Clamp(low, 0, 1), High = Math.Clamp(high, 0, 1), LeftTrigger = Math.Clamp(lt, 0, 1), RightTrigger = Math.Clamp(rt, 0, 1) };
            Slot<RumbleFn>(d.Ptr, 10)(d.Ptr, ref r);
            return true;
        }
        catch { return false; }
    }

    private static void Release(IntPtr p) { try { if (p != IntPtr.Zero) Slot<RefFn>(p, 2)(p); } catch { } }

    public void Dispose()
    {
        foreach (var d in _devices) { try { SetRumble(d, 0, 0, 0, 0); } catch { } Release(d.Ptr); d.Ptr = IntPtr.Zero; }
        _devices.Clear();
        Release(_gi); _gi = IntPtr.Zero;
    }
}
