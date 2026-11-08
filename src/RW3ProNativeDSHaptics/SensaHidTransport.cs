using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Writes Sensa haptic frames straight to the Wolverine V3 Pro over USB, the way Synapse does
/// (docs/SENSA_USB_PROTOCOL.md, captured 2026-11-08): HID SET_REPORT Feature, report ID 4, on the
/// Razer control collection (interface 1, COL04, feature length 91). Razer packet: status 0,
/// transaction counter, data size = 1 + frame length, command class 0x16, id 0x0E, arg 0x01,
/// frame, XOR CRC over packet bytes 2..87.
/// No driver, no admin rights, no Synapse; only frames whose format we know are sent.
/// </summary>
public sealed class SensaHidTransport : IDisposable
{
    public const ushort RazerVid = 0x1532, WolverinePcPid = 0x0A4C;
    public const byte ReportId = 0x04, CommandClass = 0x16, CommandId = 0x0E;
    public const int FeatureLength = 91;          // report ID + 90-byte Razer packet
    public const int MaxFrameLength = 80 - 1;     // 80 argument bytes, first is 0x01

    private SafeFileHandle? _handle;
    private readonly byte[] _report = new byte[FeatureLength];
    private byte _sequence;

    public string? DevicePath { get; private set; }
    public long FramesSent { get; private set; }
    public int LastError { get; private set; }
    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    /// <summary>All present Razer control collections of a Wolverine in PC mode (cable and/or HyperSpeed dongle).</summary>
    public static System.Collections.Generic.List<string> Candidates()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (string path in Hid.EnumeratePaths())
        {
            string p = path.ToLowerInvariant();
            if (p.Contains("vid_1532&pid_0a4c") && p.Contains("&mi_01") && p.Contains("&col04")) list.Add(path);
        }
        return list;
    }

    /// <summary>Opens the first candidate (or <paramref name="preferredPath"/>). False when none answers.</summary>
    public bool Open(string? preferredPath = null)
    {
        Close();
        var candidates = Candidates();
        if (preferredPath != null) { candidates.Remove(preferredPath); candidates.Insert(0, preferredPath); }
        foreach (string path in candidates)
        {
            // Feature reports work on handles opened without read/write access (keyboard-class rules);
            // try read/write first, fall back to query-only.
            var h = Hid.CreateFile(path, Hid.GenericRead | Hid.GenericWrite, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); h = Hid.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); }
            if (h.IsInvalid) { LastError = Marshal.GetLastWin32Error(); h.Dispose(); continue; }
            if (!Hid.HasFeatureLength(h, FeatureLength)) { h.Dispose(); continue; }
            _handle = h; DevicePath = path;
            return true;
        }
        return false;
    }

    /// <summary>Builds the 91-byte feature report for one frame (pure; also used by tests).</summary>
    public static void BuildReport(ReadOnlySpan<byte> frame, byte sequence, Span<byte> report)
    {
        if (report.Length < FeatureLength) throw new ArgumentException("report buffer too small");
        if (frame.Length == 0 || frame.Length > MaxFrameLength) throw new ArgumentException("frame length " + frame.Length);
        report.Slice(0, FeatureLength).Clear();
        report[0] = ReportId;
        var pkt = report.Slice(1, 90);
        pkt[0] = 0x00;                       // status
        pkt[1] = sequence;                   // transaction counter
        pkt[5] = (byte)(frame.Length + 1);   // data size
        pkt[6] = CommandClass;
        pkt[7] = CommandId;
        pkt[8] = 0x01;
        frame.CopyTo(pkt.Slice(9));
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= pkt[i];
        pkt[88] = crc;
    }

    /// <summary>Sends one frame. Returns false on a write failure (device gone, mode switched).</summary>
    public bool Send(ReadOnlySpan<byte> frame)
    {
        if (!IsOpen) return false;
        BuildReport(frame, _sequence, _report);
        if (!Hid.HidD_SetFeature(_handle!, _report, _report.Length))
        {
            LastError = Marshal.GetLastWin32Error();
            return false;
        }
        _sequence = (byte)(_sequence + 1);
        FramesSent++;
        return true;
    }

    public void Close() { _handle?.Dispose(); _handle = null; DevicePath = null; }
    public void Dispose() => Close();

    internal static class Hid
    {
        public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [StructLayout(LayoutKind.Sequential)]
        private struct Caps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort A, B, C, D, E, F, G, H, I, J;
        }

        public static int OutputLength(SafeFileHandle h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pd)) return 0;
            try { return HidP_GetCaps(pd, out var caps) == 0x00110000 ? caps.OutputReportByteLength : 0; }
            finally { HidD_FreePreparsedData(pd); }
        }

        public static int InputLength(SafeFileHandle h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pd)) return 0;
            try { return HidP_GetCaps(pd, out var caps) == 0x00110000 ? caps.InputReportByteLength : 0; }
            finally { HidD_FreePreparsedData(pd); }
        }

        public static bool HasFeatureLength(SafeFileHandle h, int length)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pd)) return false;
            try { return HidP_GetCaps(pd, out var caps) == 0x00110000 && caps.FeatureReportByteLength == length; }
            finally { HidD_FreePreparsedData(pd); }
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }

        public static System.Collections.Generic.IEnumerable<string> EnumeratePaths()
        {
            HidD_GetHidGuid(out Guid guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);   // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) yield break;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var d = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref d)) break;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out _, IntPtr.Zero)) continue;
                        string? path = Marshal.PtrToStringUni(buf + 4);
                        if (path != null) yield return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
