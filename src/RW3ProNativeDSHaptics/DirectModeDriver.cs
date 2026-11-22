using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Switches the Wolverine's Xbox-mode USB instance(s) (USB\VID_1532&amp;PID_0A3F\…) between Windows' Xbox controller driver
/// (dc1-controller) and Microsoft's inbox WinUSB, for direct mode (docs/WINUSB_DIRECT_MODE.md). No INF of our own, no
/// catalog, no certificate: install selects "WinUsb Device" from the inbox, signed winusb.inf for that instance (what
/// Device Manager does with "Let me pick → Universal Serial Bus devices → WinUsb Device"); removal uninstalls the instance
/// and rescans, so Windows picks its best match, dc1-controller, again. Needs an elevated process. Every switch is
/// recorded in direct-mode.json so removal knows what to undo.
/// </summary>
public static class DirectModeDriver
{
    public const string HardwarePrefix = @"USB\VID_1532&PID_0A3F\";
    private static readonly Guid UsbDeviceClass = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

    public sealed record Instance(string Id, string Service, bool Present);
    public sealed record StateEntry(string Id, string PreviousService, DateTime InstalledAt);

    public static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "direct-mode.json");

    public static List<StateEntry> LoadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<List<StateEntry>>(File.ReadAllText(StateFile)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveState(List<StateEntry> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        if (state.Count == 0) { try { File.Delete(StateFile); } catch { } return; }
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Every Xbox-mode Wolverine instance Windows knows (present or not), with its current service.</summary>
    public static List<Instance> Instances()
    {
        var list = new List<Instance>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x04 /*ALLCLASSES*/);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var did = NewDid();
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                var id = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref did, id, id.Capacity, out _)) continue;
                string s = id.ToString();
                if (!s.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new Instance(s, Property(set, ref did, 0x04 /*SPDRP_SERVICE*/) ?? "-", CM_Get_DevNode_Status(out _, out _, did.DevInst, 0) == 0));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    public static bool AnyOnWinUsb() => Instances().Exists(i => i.Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts every present Xbox-mode instance on WinUSB. Returns a short report; throws nothing.</summary>
    public static (bool Ok, string Message) Install()
    {
        var report = new StringBuilder();
        var state = LoadState();
        bool any = false, allOk = true;
        foreach (var inst in Instances())
        {
            if (!inst.Present) continue;
            if (inst.Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase)) { report.AppendLine(inst.Id + ": zaten WinUSB"); any = true; continue; }
            any = true;
            var (ok, msg) = InstallOne(inst.Id);
            report.AppendLine(inst.Id + ": " + msg);
            DiagnosticsService.Info("Direct mode install " + inst.Id + " (was " + inst.Service + "): " + msg);
            if (ok) { state.RemoveAll(e => e.Id == inst.Id); state.Add(new StateEntry(inst.Id, inst.Service, DateTime.Now)); }
            allOk &= ok;
        }
        SaveState(state);
        if (!any) return (false, "Xbox modunda bağlı bir Wolverine bulunamadı (kolu Xbox modunda tak).");
        return (allOk, report.ToString().TrimEnd());
    }

    /// <summary>Puts every Xbox-mode instance that is on WinUSB (and everything recorded) back on Windows' own driver.</summary>
    public static (bool Ok, string Message) Remove()
    {
        var report = new StringBuilder();
        var state = LoadState();
        bool allOk = true, any = false;
        foreach (var inst in Instances())
        {
            bool recorded = state.Exists(e => e.Id.Equals(inst.Id, StringComparison.OrdinalIgnoreCase));
            if (!recorded && !inst.Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            var (ok, msg) = UninstallOne(inst.Id);
            report.AppendLine(inst.Id + ": " + msg);
            DiagnosticsService.Info("Direct mode remove " + inst.Id + ": " + msg);
            if (ok) state.RemoveAll(e => e.Id.Equals(inst.Id, StringComparison.OrdinalIgnoreCase));
            allOk &= ok;
        }
        Rescan();
        SaveState(state);
        if (!any) return (true, "Doğrudan mod zaten kurulu değil.");
        var after = Instances().FindAll(i => i.Present);
        report.AppendLine("Şimdi: " + (after.Count == 0 ? "kol bağlı değil (takınca Windows kendi sürücüsünü kurar)" : string.Join(", ", after.ConvertAll(i => i.Service))));
        return (allOk, report.ToString().TrimEnd());
    }

    private static (bool, string) InstallOne(string instanceId)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return (false, "SetupAPI açılamadı");
        try
        {
            var did = NewDid();
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref did)) return (false, "aygıt bulunamadı " + Marshal.GetLastWin32Error());
            // 1. our interface GUID, read by WinUSB when it starts, so RW3 finds the pad
            IntPtr key = SetupDiCreateDevRegKey(set, ref did, 1 /*GLOBAL*/, 0, 1 /*DIREG_DEV*/, IntPtr.Zero, null);
            if (key != new IntPtr(-1))
            {
                byte[] v = Encoding.Unicode.GetBytes(WinUsbGipTransport.InterfaceGuid.ToString("B").ToUpperInvariant() + "\0\0");
                RegSetValueEx(key, "DeviceInterfaceGUIDs", 0, 7 /*REG_MULTI_SZ*/, v, v.Length);
                RegCloseKey(key);
            }
            // 2. the inbox winusb.inf, single-INF class driver list (WinUSB's class is USBDevice)
            string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "winusb.inf");
            var p = NewParams();
            if (!SetupDiGetDeviceInstallParams(set, ref did, ref p)) return (false, "parametre okunamadı " + Marshal.GetLastWin32Error());
            p.Flags |= 0x00010000;   // DI_ENUMSINGLEINF
            p.FlagsEx |= 0x00000800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            p.DriverPath = inf;
            if (!SetupDiSetDeviceInstallParams(set, ref did, ref p)) return (false, "parametre yazılamadı " + Marshal.GetLastWin32Error());
            var cls = UsbDeviceClass.ToByteArray();
            string clsText = UsbDeviceClass.ToString("B").ToUpperInvariant() + "\0";
            var clsBytes = Encoding.Unicode.GetBytes(clsText);
            SetupDiSetDeviceRegistryProperty(set, ref did, 0x08 /*SPDRP_CLASSGUID*/, clsBytes, (uint)clsBytes.Length);
            did.ClassGuid = UsbDeviceClass;
            if (!SetupDiBuildDriverInfoList(set, ref did, 1 /*SPDIT_CLASSDRIVER*/)) return (false, "sürücü listesi kurulamadı " + Marshal.GetLastWin32Error());
            var drv = NewDrv();
            bool found = false;
            for (uint i = 0; SetupDiEnumDriverInfo(set, ref did, 1, i, ref drv); i++)
                if (drv.Description.Contains("WinUsb", StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            if (!found) return (false, "winusb.inf içinde WinUsb Device bulunamadı");
            if (!SetupDiSetSelectedDriver(set, ref did, ref drv)) return (false, "sürücü seçilemedi " + Marshal.GetLastWin32Error());
            if (!DiInstallDevice(IntPtr.Zero, set, ref did, ref drv, 0x02 /*NOFINISHINSTALLUI*/, out bool reboot))
                return (false, "kurulum başarısız " + Marshal.GetLastWin32Error());
            if (reboot) return (false, "Windows değişikliği tamamlamak için yeniden başlatma istiyor" + VetoHint() + ". O uygulamayı ve RW3'ü (tepsiden) kapatıp bilgisayarı yeniden başlat; " +
                "bu arada Doğrudan modu kaldır'a basma (iki sürücü işlemi üst üste biner).");
            return (true, "WinUSB kuruldu");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static (bool, string) UninstallOne(string instanceId)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return (false, "SetupAPI açılamadı");
        try
        {
            var did = NewDid();
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref did)) return (true, "kayıt yok (zaten kaldırılmış)");
            if (!DiUninstallDevice(IntPtr.Zero, set, ref did, 0, out bool reboot)) return (false, "kaldırılamadı " + Marshal.GetLastWin32Error());
            if (reboot) return (false, "kaldırma Windows yeniden başlatılınca tamamlanacak" + VetoHint() + "; RW3'ü tepsiden kapatıp yeniden başlat");
            return (true, "kaldırıldı, Windows kendi sürücüsünü kuracak");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Which application stopped the last device removal (System log, Kernel-PnP event 225, last 5 minutes):
    /// e.g. Ubisoft Connect's upc.exe held the pad on 2026-11-22. Empty when none is found.</summary>
    /// <summary>Names the apps that vetoed a device removal in the last 5 minutes (System log, Kernel-PnP 225), e.g.
    /// " (kolu tutan uygulama: upc.exe, GameInputRedistService.exe; onu kapat)". The event can land a moment after the
    /// call returns, so it looks twice. 2026-11-23: the old parser split the path at its first space and said "Program".</summary>
    public static string VetoHint()
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=225) and TimeCreated[timediff(@SystemTime) <= 300000]]]") { ReverseDirection = true };
                using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                var apps = new List<string>();
                for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                    using (e)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(e.FormatDescription() ?? "", @"\\([^\\]+?\.exe)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success && !apps.Exists(x => x.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))) apps.Add(m.Groups[1].Value);
                    }
                if (apps.Count > 0) return " (kolu tutan uygulama: " + string.Join(", ", apps) + "; onu kapat)";
                System.Threading.Thread.Sleep(700);
            }
        }
        catch { }
        return "";
    }

    /// <summary>"Scan for hardware changes": Windows re-detects the pad and installs its best matching driver.</summary>
    /// Asynchronous: the synchronous root rescan never returned on 2026-11-22 and left the Settings card on
    /// "Kaldırılıyor…" although the pad was already back on dc1-controller. Then waits up to 8 s for the pad to settle.
    public static void Rescan()
    {
        if (CM_Locate_DevNode(out uint root, null, 0) == 0) CM_Reenumerate_DevNode(root, 0x4 /*ASYNCHRONOUS*/);
        for (int i = 0; i < 16; i++)
        {
            System.Threading.Thread.Sleep(500);
            if (Instances().Exists(x => x.Present && !x.Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase))) break;
        }
    }

    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA did, uint prop)
    {
        var buf = new byte[1024];
        return SetupDiGetDeviceRegistryProperty(set, ref did, prop, out _, buf, (uint)buf.Length, out uint n) && n > 2
            ? Encoding.Unicode.GetString(buf, 0, (int)n).TrimEnd('\0') : null;
    }

    private static SP_DEVINFO_DATA NewDid() => new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
    private static SP_DEVINSTALL_PARAMS NewParams() => new() { cbSize = Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = "" };
    private static SP_DRVINFO_DATA NewDrv() => new() { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>(), Description = "", MfgName = "", ProviderName = "" };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public int cbSize; public int Flags; public int FlagsEx; public IntPtr hwndParent; public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext; public IntPtr FileQueue; public UIntPtr ClassInstallReserved; public int Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA
    {
        public uint cbSize; public uint DriverType; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate; public ulong DriverVersion;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA did);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA did, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string id, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA did);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA did, uint prop, out uint type, byte[] buf, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA did, uint prop, byte[] buf, uint size);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiCreateDevRegKey(IntPtr set, ref SP_DEVINFO_DATA did, uint scope, uint profile, uint keyType, IntPtr infHandle, string? section);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA did, ref SP_DEVINSTALL_PARAMS p);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiSetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA did, ref SP_DEVINSTALL_PARAMS p);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA did, uint type);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiEnumDriverInfo(IntPtr set, ref SP_DEVINFO_DATA did, uint type, uint index, ref SP_DRVINFO_DATA drv);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiSetSelectedDriver(IntPtr set, ref SP_DEVINFO_DATA did, ref SP_DRVINFO_DATA drv);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA did, ref SP_DRVINFO_DATA drv, uint flags, out bool reboot);
    [DllImport("newdev.dll", SetLastError = true)] private static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA did, uint flags, out bool reboot);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNode(out uint devInst, string? id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Reenumerate_DevNode(uint devInst, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(IntPtr key, string name, int reserved, int type, byte[] data, int size);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);
}
