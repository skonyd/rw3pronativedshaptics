using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Diagnostic for the virtual pads: reads OUR virtual pad back the way a GDK game does (GameInput, gamepad kind) and
/// counts reading changes, so the log shows where input stops: physical read → virtual pad (XInput) → GameInput.
/// 2026-11-15: Banishers listed the virtual Xbox Series pad as a gamepad (GameInput family XboxOne) but did not react
/// to it. Inbox GameInput v0 vtable (IGameInput 4 GetCurrentReading, 9 RegisterDeviceCallback, 13 Unregister;
/// IGameInputDevice 3 GetDeviceInfo; IGameInputReading 22 GetGamepadState).
/// </summary>
public sealed class VirtualPadInputProbe : IDisposable
{
    private const uint KindGamepad = 0x00040000;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr InfoFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadingFn(IntPtr self, uint kind, IntPtr device, out IntPtr reading);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeviceCallback(ulong token, IntPtr context, IntPtr device, ulong timestamp, uint current, uint previous);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterFn(IntPtr self, IntPtr device, uint kind, uint status, int enumeration, IntPtr context, DeviceCallback callback, out ulong token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool UnregisterFn(IntPtr self, ulong token, ulong timeout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate bool StateFn(IntPtr self, out Pad state);
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public uint Buttons; public float LT, RT, LX, LY, RX, RY; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("GameInput.dll", ExactSpelling = true)] private static extern int GameInputCreate(out IntPtr gameInput);
    private static T Slot<T>(IntPtr o, int i) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o), i * IntPtr.Size));

    private IntPtr _gi, _device;
    private Pad _last;
    private bool _haveLast;
    public long Changes { get; private set; }
    public string Device { get; private set; } = "yok";
    public string LastError { get; private set; } = "";
    /// <summary>GameInput device ids present before our virtual pad was created (a real Bluetooth Xbox pad looks the same).</summary>
    public HashSet<string>? Exclude { get; init; }

    private static string IdOf(IntPtr info)
    {
        var b = new byte[32]; Marshal.Copy(info + 32, b, 0, 32); return Convert.ToHexString(b);
    }

    /// <summary>Ids of the gamepads GameInput lists right now.</summary>
    public static HashSet<string> DeviceIds()
    {
        var ids = new HashSet<string>();
        if (GameInputCreate(out var gi) < 0 || gi == IntPtr.Zero) return ids;
        try
        {
            DeviceCallback cb = (_, _, d, _, cur, _) => { try { if ((cur & 1) != 0) lock (ids) ids.Add(IdOf(Slot<InfoFn>(d, 3)(d))); } catch { } };
            if (Slot<RegisterFn>(gi, 9)(gi, IntPtr.Zero, KindGamepad, 1, 2, IntPtr.Zero, cb, out ulong token) >= 0) Slot<UnregisterFn>(gi, 13)(gi, token, ulong.MaxValue);
            GC.KeepAlive(cb);
        }
        finally { Slot<RefFn>(gi, 2)(gi); }
        return ids;
    }

    /// <summary>Finds a gamepad that is not the Wolverine (1532:*) and not a Sony pad: our virtual Xbox pad.</summary>
    public bool TryOpen()
    {
        if (_device != IntPtr.Zero) return true;
        try
        {
            if (_gi == IntPtr.Zero && GameInputCreate(out _gi) < 0) { LastError = "GameInputCreate"; return false; }
            IntPtr pick = IntPtr.Zero; string name = "";
            DeviceCallback cb = (_, _, d, _, cur, _) =>
            {
                try
                {
                    if ((cur & 1) == 0 || pick != IntPtr.Zero) return;
                    IntPtr info = Slot<InfoFn>(d, 3)(d);
                    ushort vid = (ushort)Marshal.ReadInt16(info, 4), pid = (ushort)Marshal.ReadInt16(info, 6);
                    if (vid == 0x1532 || vid == 0x054C) return;
                    if (Exclude != null && Exclude.Contains(IdOf(info))) return;   // was there before our pad: not ours
                    Slot<RefFn>(d, 1)(d); pick = d; name = $"{vid:X4}:{pid:X4} family {Marshal.ReadInt32(info, 96)}";
                }
                catch { }
            };
            int r = Slot<RegisterFn>(_gi, 9)(_gi, IntPtr.Zero, KindGamepad, 1, 2, IntPtr.Zero, cb, out ulong token);
            if (r >= 0) Slot<UnregisterFn>(_gi, 13)(_gi, token, ulong.MaxValue);
            GC.KeepAlive(cb);
            _device = pick; Device = pick != IntPtr.Zero ? name : "GameInput'ta sanal gamepad yok";
            return pick != IntPtr.Zero;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>One reading; counts it when buttons/sticks/triggers changed.</summary>
    public void Poll()
    {
        if (!TryOpen()) return;
        IntPtr reading = IntPtr.Zero;
        try
        {
            if (Slot<ReadingFn>(_gi, 4)(_gi, KindGamepad, _device, out reading) < 0 || reading == IntPtr.Zero) return;
            if (!Slot<StateFn>(reading, 22)(reading, out var s)) return;
            if (_haveLast && (s.Buttons != _last.Buttons || Math.Abs(s.LX - _last.LX) > 0.02 || Math.Abs(s.LY - _last.LY) > 0.02 ||
                Math.Abs(s.RX - _last.RX) > 0.02 || Math.Abs(s.RY - _last.RY) > 0.02 || Math.Abs(s.LT - _last.LT) > 0.02 || Math.Abs(s.RT - _last.RT) > 0.02))
                Changes++;
            _last = s; _haveLast = true;
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally { if (reading != IntPtr.Zero) Slot<RefFn>(reading, 2)(reading); }
    }

    public void Dispose()
    {
        try { if (_device != IntPtr.Zero) Slot<RefFn>(_device, 2)(_device); } catch { }
        try { if (_gi != IntPtr.Zero) Slot<RefFn>(_gi, 2)(_gi); } catch { }
        _device = _gi = IntPtr.Zero;
    }
}
