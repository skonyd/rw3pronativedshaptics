using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Experiment (2026-11-22): trigger rumble written as a HID output report straight to the Wolverine's "XINPUT compatible
/// HID" child (HID\VID_045E&amp;PID_02FF&amp;IG_00…). GameInput / Windows.Gaming.Input / Sensa GIP output from RW3 is dropped
/// while another controller app is in front (E-20261122-52…54); a plain HID write may not pass that focus gate. The
/// report is built from the device's own output caps (Physical Interface page: DC Enable Actuators 0x97, Magnitude 0x70
/// ×4 = [left trigger, right trigger, left body, right body], Duration 0x50, Start Delay 0xA7, Loop Count 0x7C), so a
/// device without such a report is only described in the log, never written to.
/// </summary>
public sealed class GipHidTriggerOutput : IDisposable
{
    private SafeFileHandle? _handle;
    private IntPtr _preparsed;
    private byte _reportId;
    private int _outLen, _magMax = 100, _magCount;
    private long _nextOpen;
    public long Writes { get; private set; }
    public long Failures { get; private set; }
    public string State { get; private set; } = "-";
    public string Description { get; private set; } = "";
    public bool Usable => _handle is { IsInvalid: false } && _magCount >= 4;

    /// <summary>Finds and opens the pad's HID child, reads its output caps; true when a 4-magnitude output report exists.</summary>
    public bool TryOpen()
    {
        if (Usable) return true;
        long now = Environment.TickCount64;
        if (now < _nextOpen) return false;
        _nextOpen = now + 5000;
        Close();
        var sb = new StringBuilder();
        foreach (var path in HidPaths())
        {
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            bool rw = !h.IsInvalid;
            if (!rw) { h.Dispose(); h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); }
            if (h.IsInvalid) { sb.Append($"[{Short(path)} open failed {Marshal.GetLastWin32Error()}] "); h.Dispose(); continue; }
            if (!HidD_GetPreparsedData(h, out var pp)) { sb.Append($"[{Short(path)} no preparsed data] "); h.Dispose(); continue; }
            HidP_GetCaps(pp, out var caps);
            sb.Append($"[{Short(path)} rw {rw} usage {caps.UsagePage:X2}/{caps.Usage:X2} in {caps.InputReportByteLength} out {caps.OutputReportByteLength} feat {caps.FeatureReportByteLength}");
            int mag = 0, max = 100; byte rid = 0;
            if (caps.NumberOutputValueCaps > 0)
            {
                ushort n = caps.NumberOutputValueCaps;
                var vc = new HIDP_VALUE_CAPS[n];
                if (HidP_GetValueCaps(1 /*Output*/, vc, ref n, pp) == HIDP_STATUS_SUCCESS)
                    foreach (var v in vc.AsSpan(0, n))
                    {
                        ushort u = v.IsRange != 0 ? v.UsageMin : v.UsageMin;
                        sb.Append($" ·out {v.UsagePage:X2}:{u:X2} rid {v.ReportID} {v.BitSize}b×{v.ReportCount} max {v.LogicalMax}");
                        if (v.UsagePage == 0x0F && u == 0x70) { mag = v.ReportCount; max = v.LogicalMax > 0 ? v.LogicalMax : 100; rid = v.ReportID; }
                    }
            }
            sb.Append("] ");
            if (rw && mag >= 4 && caps.OutputReportByteLength > 0)
            {
                _handle = h; _preparsed = pp; _outLen = caps.OutputReportByteLength; _magCount = mag; _magMax = max; _reportId = rid;
                State = $"HID 02FF ok (rid {rid}, {mag} magnitudes, max {max})";
                Description = sb.ToString();
                return true;
            }
            HidD_FreePreparsedData(pp); h.Dispose();
        }
        Description = sb.Length == 0 ? "no HID\\VID_045E&PID_02FF&IG_ node present" : sb.ToString();
        State = "HID yolu yok";
        return false;
    }

    /// <summary>Writes [enable all, LT, RT, left body, right body] (0..1) with the longest duration.</summary>
    public bool Set(double bodyLeft, double bodyRight, double triggerLeft, double triggerRight)
    {
        if (!TryOpen()) return false;
        var report = new byte[_outLen];
        if (HidP_InitializeReportForID(1, _reportId, _preparsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) { Failures++; return false; }
        byte Q(double v) => (byte)Math.Round((double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0) * _magMax);
        var mags = new byte[_magCount];
        mags[0] = Q(triggerLeft); mags[1] = Q(triggerRight); mags[2] = Q(bodyLeft); mags[3] = Q(bodyRight);
        HidP_SetUsageValue(1, 0x0F, 0, 0x97, 0x0F, _preparsed, report, (uint)report.Length);   // DC Enable Actuators: all four
        int st = HidP_SetUsageValueArray(1, 0x0F, 0, 0x70, mags, (ushort)mags.Length, _preparsed, report, (uint)report.Length);
        HidP_SetUsageValue(1, 0x0F, 0, 0x50, 0xFF, _preparsed, report, (uint)report.Length);   // Duration
        HidP_SetUsageValue(1, 0x0F, 0, 0xA7, 0, _preparsed, report, (uint)report.Length);      // Start Delay
        HidP_SetUsageValue(1, 0x0F, 0, 0x7C, 0xEB, _preparsed, report, (uint)report.Length);   // Loop Count
        if (st != HIDP_STATUS_SUCCESS) { Failures++; State = $"magnitude set 0x{st:X8}"; return false; }
        bool ok = WriteFile(_handle!, report, (uint)report.Length, out _, IntPtr.Zero) || HidD_SetOutputReport(_handle!, report, (uint)report.Length);
        if (ok) Writes++;
        else { Failures++; State = $"write failed {Marshal.GetLastWin32Error()}"; if (Failures % 50 == 1) Close(); }
        return ok;
    }

    private static string Short(string path) { int i = path.IndexOf("IG_", StringComparison.OrdinalIgnoreCase); return i >= 0 ? path.Substring(i, Math.Min(24, path.Length - i)) : path; }

    /// <summary>Device paths of the present HID\VID_045E&amp;PID_02FF&amp;IG_* nodes (the GIP pads' XInput HID children).</summary>
    private static List<string> HidPaths()
    {
        var list = new List<string>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|DEVICEINTERFACE*/);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (path.Contains("vid_045e&pid_02ff&ig_", StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private void Close()
    {
        if (_preparsed != IntPtr.Zero) { HidD_FreePreparsedData(_preparsed); _preparsed = IntPtr.Zero; }
        _handle?.Dispose(); _handle = null; _magCount = 0;
    }

    public void Dispose()
    {
        try { if (Usable) Set(0, 0, 0, 0); } catch { }
        Close();
    }

    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage; public byte ReportID; public byte IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount;
        public ushort R1, R2, R3, R4, R5;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] b, uint len);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr p);
    [DllImport("hid.dll")] private static extern int HidP_InitializeReportForID(int type, byte id, IntPtr p, byte[] report, uint len);
    [DllImport("hid.dll")] private static extern int HidP_SetUsageValue(int type, ushort page, ushort link, ushort usage, uint value, IntPtr p, byte[] report, uint len);
    [DllImport("hid.dll")] private static extern int HidP_SetUsageValueArray(int type, ushort page, ushort link, ushort usage, byte[] values, ushort valuesLen, IntPtr p, byte[] report, uint len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr ov);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
