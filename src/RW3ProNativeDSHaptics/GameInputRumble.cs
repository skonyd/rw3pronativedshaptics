using System;
using System.Runtime.InteropServices;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Xbox-mode motors through GameInput (inbox System32\GameInput.dll, v0 ABI): body motors and the
/// impulse-trigger motors. Measured 2026-11-08 with the webcam mic on the Wolverine in Xbox mode:
/// triggers +27.4 / +19.4 dB and body +18.2 dB while this process was in the BACKGROUND — unlike
/// Windows.Gaming.Input, which only vibrates for the foreground window (0 dB in the background).
/// Only the Wolverine (VID 1532) is driven. Interop by vtable slot (IUnknown 0–2):
/// IGameInput 4 GetCurrentReading; IGameInputReading 6 GetDevice; IGameInputDevice 3 GetDeviceInfo, 10 SetRumbleState.
/// </summary>
public sealed class GameInputRumble : IDisposable
{
    public const uint KindGamepad = 0x00040000;
    public bool HasDevice => _device != IntPtr.Zero;
    public ushort ProductId { get; private set; }
    public string LastError { get; private set; } = "";
    public long Writes { get; private set; }

    private IntPtr _gi, _device;
    private Rumble _last;
    private long _lastWriteTick;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rumble { public float Low, High, LeftTrigger, RightTrigger; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCurrentReadingFn(IntPtr self, uint kind, IntPtr device, out IntPtr reading);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDeviceFn(IntPtr self, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetDeviceInfoFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetRumbleStateFn(IntPtr self, ref Rumble rumble);

    [DllImport("GameInput.dll")] private static extern int GameInputCreate(out IntPtr gameInput);

    private static T Slot<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    private static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try { Slot<ReleaseFn>(obj, 2)(obj); } catch { }
        obj = IntPtr.Zero;
    }

    /// <summary>Finds the Wolverine among GameInput gamepads. Cheap to call again after a failure.</summary>
    public bool TryAcquire()
    {
        if (_device != IntPtr.Zero) return true;
        try
        {
            if (_gi == IntPtr.Zero)
            {
                int hr = GameInputCreate(out _gi);
                if (hr != 0) { LastError = $"GameInputCreate 0x{hr:X8}"; _gi = IntPtr.Zero; return false; }
            }
            int r = Slot<GetCurrentReadingFn>(_gi, 4)(_gi, KindGamepad, IntPtr.Zero, out IntPtr reading);
            if (r != 0 || reading == IntPtr.Zero) { LastError = $"no gamepad reading 0x{r:X8}"; return false; }
            Slot<GetDeviceFn>(reading, 6)(reading, out IntPtr device);
            Release(ref reading);
            if (device == IntPtr.Zero) { LastError = "reading without device"; return false; }
            IntPtr info = Slot<GetDeviceInfoFn>(device, 3)(device);
            ushort vid = info == IntPtr.Zero ? (ushort)0 : (ushort)Marshal.ReadInt16(info, 4);
            ushort pid = info == IntPtr.Zero ? (ushort)0 : (ushort)Marshal.ReadInt16(info, 6);
            if (vid != 0x1532) { LastError = $"gamepad {vid:X4}:{pid:X4} is not the Wolverine"; Release(ref device); return false; }
            _device = device; ProductId = pid; LastError = "";
            return true;
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>Sets all four motors (0..1). Writes on change, plus a 100 ms keep-alive.</summary>
    public void Set(double low, double high, double leftTrigger, double rightTrigger)
    {
        if (_device == IntPtr.Zero) return;
        var r = new Rumble { Low = Q(low), High = Q(high), LeftTrigger = Q(leftTrigger), RightTrigger = Q(rightTrigger) };
        long now = Environment.TickCount64;
        if (r.Equals(_last) && now - _lastWriteTick < 100) return;
        try { Slot<SetRumbleStateFn>(_device, 10)(_device, ref r); Writes++; _last = r; _lastWriteTick = now; }
        catch (Exception ex) { LastError = ex.Message; Release(ref _device); }
    }

    private static float Q(double v) => (float)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * 100) / 100f;

    /// <summary>Drops the device (e.g. the controller left Xbox mode); the next TryAcquire looks again.</summary>
    public void Forget() { Set(0, 0, 0, 0); Release(ref _device); }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) { try { var z = new Rumble(); Slot<SetRumbleStateFn>(_device, 10)(_device, ref z); } catch { } }
        Release(ref _device);
        Release(ref _gi);
    }
}
