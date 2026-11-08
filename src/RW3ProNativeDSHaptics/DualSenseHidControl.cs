using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Wakes the USB DualSense's haptic/audio path with one HID output report (ID 0x02).
///
/// Observed 2026-11-07: rear-channel PCM reached the DualSense endpoint (loopback RMS 0.33 on
/// channel 3, Exclusive PCM16 also accepted) yet the actuators did not move. The controller's
/// MuteControl byte can hold HapticPowerSave / AudioPowerSave / HapticMute; Special K's
/// initialize_audio() clears them with AllowAudioMute=1 before using audio haptics.
///
/// Layout (USB, report ID at [0]; SetStateData starts at [1]):
///   [1] flags0: bit0 EnableRumbleEmulation, bit1 UseRumbleNotHaptics → both 0 = audio haptics
///   [2] flags1: bit1 AllowAudioMute
///   [10] MuteControl: bit0 TouchPowerSave, 1 MotionPowerSave, 2 HapticPowerSave,
///        3 AudioPowerSave, 4 MicMute, 5 SpeakerMute, 6 HeadphoneMute, 7 HapticMute → all 0
/// Sources: Special K src/input/hid_reports/playstation.cpp (SK_HID_DualSense_SetStateData),
/// Linux hid-playstation.c (DS_OUTPUT_REPORT_USB = 0x02, valid_flag1 bit1).
///
/// This changes only the controller's live state (lost on unplug); no driver, no persistent
/// setting. It is sent only when the user starts a DualSense test.
/// </summary>
public static class DualSenseHidControl
{
    public const byte UsbOutputReportId = 0x02;
    public const byte Flag1AllowAudioMute = 0x02;
    public const int MuteControlOffset = 10;

    /// <summary>Builds the wake report for a given output report length (incl. report ID).</summary>
    public static byte[] BuildWakeHapticsReport(int outputReportLength)
    {
        if (outputReportLength < MuteControlOffset + 1)
            throw new ArgumentOutOfRangeException(nameof(outputReportLength));
        var report = new byte[outputReportLength];
        report[0] = UsbOutputReportId;
        report[1] = 0x00;                 // no rumble emulation → controller plays audio haptics
        report[2] = Flag1AllowAudioMute;  // apply MuteControl below
        report[MuteControlOffset] = 0x00; // all power-save and mute bits cleared
        return report;
    }

    /// <summary>Sends the wake report to the first USB DualSense HID interface. Returns device path.</summary>
    public static string WakeHaptics()
    {
        string path = FindDualSenseHidPath() ?? throw new InvalidOperationException(
            "DualSense HID arayüzü bulunamadı (USB ile bağlı mı? HidHide gizliyor olabilir).");
        using SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("DualSense HID açılamadı: " + new Win32Exception(Marshal.GetLastWin32Error()).Message +
                                  " (HidHide bu uygulamayı engelliyor olabilir).");
        int length = 64;
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                if (HidP_GetCaps(preparsed, out HidpCaps caps) == HidpStatusSuccess && caps.OutputReportByteLength > 0)
                    length = caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        byte[] report = BuildWakeHapticsReport(length);
        if (!WriteFile(handle, report, report.Length, out int written, IntPtr.Zero) || written != report.Length)
            throw new IOException("DualSense HID raporu yazılamadı: " +
                                  new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return path;
    }

    /// <summary>
    /// Repeats the wake report every <paramref name="intervalMs"/> until cancelled. DSX and
    /// DualSenseY (duaLib) write output reports continuously; a single report did not make the
    /// actuators move on real hardware (2026-11-07), so the path is kept alive while playing.
    /// Returns the number of reports written.
    /// </summary>
    public static int KeepAwake(CancellationToken token, int intervalMs = 20)
    {
        string path = FindDualSenseHidPath() ?? throw new InvalidOperationException(
            "DualSense HID arayüzü bulunamadı (USB ile bağlı mı? HidHide gizliyor olabilir).");
        using SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("DualSense HID açılamadı: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        int length = 64;
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                if (HidP_GetCaps(preparsed, out HidpCaps caps) == HidpStatusSuccess && caps.OutputReportByteLength > 0)
                    length = caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        byte[] report = BuildWakeHapticsReport(length);
        int count = 0;
        while (!token.IsCancellationRequested)
        {
            if (WriteFile(handle, report, report.Length, out int written, IntPtr.Zero) && written == report.Length)
                count++;
            token.WaitHandle.WaitOne(intervalMs);
        }
        return count;
    }

    public static string? FindDualSenseHidPath()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero)) continue;
                    string? p = Marshal.PtrToStringUni(detail + 4);
                    if (p == null) continue;
                    string lower = p.ToLowerInvariant();
                    // DualSense (0CE6) or DualSense Edge (0DF2), USB (not Bluetooth "_0002054c" paths).
                    if (lower.Contains("vid_054c") && (lower.Contains("pid_0ce6") || lower.Contains("pid_0df2")))
                        return p;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    // ── interop ──
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const int DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint i, ref SpDeviceInterfaceData d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData d, IntPtr detail, int size, out int needed, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
}
