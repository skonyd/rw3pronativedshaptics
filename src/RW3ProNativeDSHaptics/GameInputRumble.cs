using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RW3ProNativeDSHaptics;

/// <summary>Inbox GameInput v0, Windows SDK 10.0.26100.0 um/GameInput.h:
/// device-bound Wolverine Xbox input and four-motor output. Never load the redist v1+ ABI.</summary>
public sealed class GameInputRumble : IDisposable
{
    public const uint KindGamepad = 0x00040000;
    private readonly object _gate = new();
    private IntPtr _gi, _device;
    private bool _disposed;
    private readonly bool _allowOutput;
    public GameInputRumble(bool allowOutput = true) => _allowOutput = allowOutput;
    private DeviceCallback? _guideCallback;
    private ulong _guideToken;
    private bool _guideRegistered;
    private int _guidePressed;
    private Rumble _last;
    private long _lastWriteTick;
    public bool HasDevice { get { lock (_gate) return Connected(); } }
    public ushort ProductId { get; private set; }
    public string LastError { get; private set; } = "";
    public string LastEnumeration { get; private set; } = "";
    public long Writes { get; private set; }
    public uint? SupportedMotorMask
    {
        get
        {
            lock (_gate)
            {
                if (!Connected()) return null;
                // GameInput SDK v0: 32-byte prefix, two 32-byte device IDs, then three DWORDs.
                const int motorMaskOffset = 108;
                IntPtr info = Slot<InfoFn>(_device, 3)(_device);
                return info != IntPtr.Zero && Marshal.ReadInt32(info) >= motorMaskOffset + sizeof(uint)
                    ? (uint)Marshal.ReadInt32(info, motorMaskOffset) : null;
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rumble { public float Low, High, LeftTrigger, RightTrigger; }
    [StructLayout(LayoutKind.Sequential)] private struct PadState
    { public uint Buttons; public float LeftTrigger, RightTrigger, LX, LY, RX, RY; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadingFn(IntPtr self, uint kind, IntPtr device, out IntPtr reading);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr InfoFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint StatusFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RumbleFn(IntPtr self, ref Rumble value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool StateFn(IntPtr self, out PadState state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FocusFn(IntPtr self, uint policy);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRawFn(IntPtr self,uint id,int kind,out IntPtr report);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint RawSizeFn(IntPtr self);
    /// <summary>Read-only capability inspection; creates a report object but never submits it or takes exclusive access.
    /// Slots14 (device CreateRawDeviceReport),4/5 (report info/size) follow the pinned SDK v0 header.</summary>
    public string InspectRawOutput(uint id=9)
    {
        lock(_gate)
        {
            if(!TryAcquire())return LastError;
            IntPtr report=IntPtr.Zero;
            try {
                int hr=Slot<CreateRawFn>(_device,14)(_device,id,1,out report);
                if(hr<0 || report==IntPtr.Zero)return $"CreateRawDeviceReport({id},Output): 0x{hr:X8}";
                IntPtr info=Slot<InfoFn>(report,4)(report);
                return info==IntPtr.Zero?"Raw report info unavailable":$"Raw report kind={Marshal.ReadInt32(info)} id={Marshal.ReadInt32(info,4)} size={Marshal.ReadInt32(info,8)} dataSize={Slot<RawSizeFn>(report,5)(report)}";
            }finally{Release(ref report);}
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeviceCallback(ulong token, IntPtr context, IntPtr device, ulong timestamp, uint current, uint previous);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterFn(IntPtr self, IntPtr device, uint kind, uint status, int enumeration, IntPtr context, DeviceCallback callback, out ulong token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterSystemFn(IntPtr self, IntPtr device, uint filter, IntPtr context, DeviceCallback callback, out ulong token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool UnregisterFn(IntPtr self, ulong token, ulong timeout);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("GameInput.dll", ExactSpelling = true)] private static extern int GameInputCreate(out IntPtr gameInput);
    private static T Slot<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));
    private static void Release(ref IntPtr obj)
    { if (obj == IntPtr.Zero) return; Slot<RefFn>(obj, 2)(obj); obj = IntPtr.Zero; }
    private bool Connected()
    {
        if (_device == IntPtr.Zero) return false;
        if ((Slot<StatusFn>(_device, 4)(_device) & 1) != 0) return true;
        DropDevice(); LastError = "Wolverine disconnected"; return false;
    }
    private void DropDevice()
    {
        if (_guideRegistered) { Slot<UnregisterFn>(_gi, 13)(_gi, _guideToken, ulong.MaxValue); _guideRegistered = false; }
        _guideCallback = null; System.Threading.Interlocked.Exchange(ref _guidePressed, 0);
        Release(ref _device);
    }
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (Connected()) return true;
            try
            {
                if (_gi == IntPtr.Zero)
                {
                    int hr = GameInputCreate(out _gi);
                    if (hr < 0 || _gi == IntPtr.Zero) { LastError = $"GameInputCreate 0x{hr:X8}"; Release(ref _gi); return false; }
                    Slot<FocusFn>(_gi, 21)(_gi, 0); // SDK v0 default; DisableBackgroundInput is not set
                }
                var found = new List<IntPtr>();
                var identities = new List<string>();
                DeviceCallback callback = (_, _, device, _, current, _) =>
                {
                    try
                    {
                        if ((current & 1) == 0) return;
                        IntPtr info = Slot<InfoFn>(device, 3)(device);
                        if (info == IntPtr.Zero || Marshal.ReadInt32(info) < 8) return;
                        ushort vid = (ushort)Marshal.ReadInt16(info, 4), pid = (ushort)Marshal.ReadInt16(info, 6);
                        lock (identities) identities.Add($"{vid:X4}:{pid:X4} status={current:X}");
                        if (!GameInputPadMapping.IsWolverine(vid, pid)) return;
                        lock (found)
                        {
                            if (found.Contains(device)) return;
                            Slot<RefFn>(device, 1)(device); found.Add(device);
                        }
                    }
                    catch { /* Never throw across the native callback boundary. */ }
                };
                // Blocking initial enumeration; finish callbacks before releasing their delegate.
                int r = Slot<RegisterFn>(_gi, 9)(_gi, IntPtr.Zero, KindGamepad, 1, 2, IntPtr.Zero, callback, out ulong token);
                if (r >= 0) Slot<UnregisterFn>(_gi, 13)(_gi, token, ulong.MaxValue);
                GC.KeepAlive(callback);
                LastEnumeration = string.Join(", ", identities);
                if (r >= 0 && found.Count == 1)
                {
                    _device = found[0]; ProductId = 0x0A3F; _lastWriteTick = 0; LastError = "";
                    // Guide is a system button, absent from GameInputGamepadState.
                    _guideCallback = (_, _, _, _, current, _) => System.Threading.Interlocked.Exchange(ref _guidePressed, (current & 1) != 0 ? 1 : 0);
                    _guideRegistered = Slot<RegisterSystemFn>(_gi, 10)(_gi, _device, 1, IntPtr.Zero, _guideCallback, out _guideToken) >= 0;
                    return true;
                }
                foreach (IntPtr p in found) { IntPtr release = p; Release(ref release); }
                LastError = r < 0 ? $"enumeration 0x{r:X8}" : found.Count == 0 ? "Wolverine 1532:0A3F not visible" : "multiple Wolverines: selection ambiguous";
                return false;
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return false; }
        }
    }
    public bool TryReadPad(out XInputPad pad)
    {
        lock (_gate)
        {
            pad = default;
            if (_disposed || !Connected()) return false;
            IntPtr reading = IntPtr.Zero;
            try
            {
                int hr = Slot<ReadingFn>(_gi, 4)(_gi, KindGamepad, _device, out reading);
                if (hr < 0 || reading == IntPtr.Zero) { LastError = $"Wolverine reading 0x{hr:X8}"; return false; }
                if (!Slot<StateFn>(reading, 22)(reading, out var s)) { LastError = "Wolverine gamepad state unavailable"; return false; }
                pad = GameInputPadMapping.Map(s.Buttons, s.LeftTrigger, s.RightTrigger, s.LX, s.LY, s.RX, s.RY);
                if (System.Threading.Volatile.Read(ref _guidePressed) != 0) pad = pad with { Buttons = (ushort)(pad.Buttons | XInputPad.Guide) };
                LastError = ""; return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
            finally { Release(ref reading); }
        }
    }
    public void Set(double low, double high, double leftTrigger, double rightTrigger)
    {
        lock (_gate)
        {
            if (_disposed || !_allowOutput || !Connected()) return;
            var r = new Rumble { Low = Q(low), High = Q(high), LeftTrigger = Q(leftTrigger), RightTrigger = Q(rightTrigger) };
            long now = Environment.TickCount64;
            if (r.Equals(_last) && now - _lastWriteTick < 100) return;
            try { Slot<RumbleFn>(_device, 10)(_device, ref r); Writes++; _last = r; _lastWriteTick = now; }
            catch (Exception ex) { LastError = ex.Message; DropDevice(); }
        }
    }
    private static float Q(double v) => (float)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * 100) / 100f;
    public void Forget() { lock (_gate) { Set(0, 0, 0, 0); DropDevice(); } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Forget(); _disposed = true; Release(ref _gi);
        }
    }
}
