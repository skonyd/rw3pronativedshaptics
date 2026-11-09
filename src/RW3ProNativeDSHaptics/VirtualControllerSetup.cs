using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
#if HAS_HIDMAESTRO
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>Health of the HIDMaestro virtual-controller stack as the Settings tab shows it.</summary>
public sealed record VirtualControllerStatus(
    bool SdkPresent, string SdkVersion, bool DriverInstalled, bool UsbipAvailable, string? Error, SelfTestResult? LastSelfTest)
{
    public bool Ready => SdkPresent && DriverInstalled && UsbipAvailable;
}

/// <summary>Result of the elevated create-and-remove check of a virtual DualSense.</summary>
public sealed record SelfTestResult(DateTime At, bool Ok, string Summary);

/// <summary>
/// Installs, checks and removes HIDMaestro (MIT, github.com/hifihedgehog/HIDMaestro v2.1.0): the
/// user-mode driver that makes a virtual DualSense look like real hardware, including its 4-channel
/// USB audio (channels 3/4 = haptic actuators, via usbip-win2) and its output reports (0x02:
/// rumble + adaptive triggers). Installing and creating virtual devices needs administrator rights,
/// so those run in an elevated copy of this exe (<see cref="RunElevatedAsync"/>); status reads do not.
/// Nothing is installed without the user pressing the button and accepting the UAC prompt.
/// </summary>
public static class VirtualControllerSetup
{
    public const string InstallArg = "--hm-install", UninstallArg = "--hm-uninstall", SelfTestArg = "--hm-selftest";
    public const string DualSenseCompositeProfile = "dualsense-composite";

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics");
    private static string ResultFile => Path.Combine(StateDir, "hidmaestro-result.json");
    private static string SelfTestFile => Path.Combine(StateDir, "hidmaestro-selftest.json");

    public const string HidHideRestoreArg = "--hidhide-restore";
    public static bool IsElevatedCommand(string? arg) => arg is InstallArg or UninstallArg or SelfTestArg or HidHideRestoreArg
        or ElevatedLaunchTask.InstallArg or ElevatedLaunchTask.UninstallArg;

    /// <summary>Read-only status (no admin, no device changes).</summary>
    public static VirtualControllerStatus Query()
    {
        SelfTestResult? last = null;
        try { if (File.Exists(SelfTestFile)) last = JsonSerializer.Deserialize<SelfTestResult>(File.ReadAllText(SelfTestFile)); } catch { }
#if HAS_HIDMAESTRO
        try
        {
            using var ctx = new HMContext();
            string ver = typeof(HMContext).Assembly.GetName().Version?.ToString(3) ?? "?";
            return new VirtualControllerStatus(true, ver, ctx.IsDriverInstalled, HMContext.IsUsbipBackendAvailable, null, last);
        }
        catch (Exception ex) { return new VirtualControllerStatus(true, "?", false, false, ex.Message, last); }
#else
        return new VirtualControllerStatus(false, "-", false, false, "HIDMaestro SDK bu derlemede yok (tools/get-hidmaestro.ps1).", last);
#endif
    }

    /// <summary>Runs one setup command in an elevated copy of this exe (UAC prompt) and returns its result.</summary>
    public static async Task<(int Code, string Message)> RunElevatedAsync(string arg, CancellationToken token = default)
    {
        Directory.CreateDirectory(StateDir);
        try { File.Delete(ResultFile); } catch { }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe path unknown");
        var psi = new ProcessStartInfo(exe, arg) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return (1223, "Yönetici izni verilmedi (UAC iptal edildi)."); }
        if (p == null) return (-1, "İşlem başlatılamadı.");
        await p.WaitForExitAsync(token);
        string msg = "Çıkış kodu " + p.ExitCode;
        try { if (File.Exists(ResultFile)) msg = File.ReadAllText(ResultFile); } catch { }
        DiagnosticsService.Info("HIDMaestro " + arg + " → " + p.ExitCode + ": " + msg);
        return (p.ExitCode, msg);
    }

    /// <summary>Entry point of the elevated process. Returns the exit code; writes a human-readable result.</summary>
    public static int RunElevatedCommand(string arg)
    {
        string message; int code;
        try { (code, message) = Execute(arg); }
        catch (UnauthorizedAccessException) { (code, message) = (5, "Yönetici yetkisi gerekli."); }
        catch (Exception ex) { (code, message) = (1, ex.GetType().Name + ": " + ex.Message); }
        try { Directory.CreateDirectory(StateDir); File.WriteAllText(ResultFile, message); } catch { }
        try { DiagnosticsService.Info("HIDMaestro elevated " + arg + ": " + message); } catch { }
        return code;
    }

    private static (int, string) Execute(string arg)
    {
        if (arg == HidHideRestoreArg) return (0, HidHideControl.Restore());
        if (arg == ElevatedLaunchTask.InstallArg) return ElevatedLaunchTask.Install();
        if (arg == ElevatedLaunchTask.UninstallArg) return ElevatedLaunchTask.Uninstall();
#if HAS_HIDMAESTRO
        switch (arg)
        {
            case InstallArg:
            {
                using var ctx = new HMContext();
                ctx.LoadDefaultProfiles();
                ctx.InstallDriver();
                var outcome = ctx.LastDriverInstall;
                string usbip = "";
                if (!HMContext.IsUsbipBackendAvailable) { HMContext.InstallUsbipBackend(s => usbip = $"{s}"); }
                bool ok = ctx.IsDriverInstalled && HMContext.IsUsbipBackendAvailable;
                return (ok ? 0 : 2, $"Sürücü: {outcome}; USB/IP arka ucu: {(HMContext.IsUsbipBackendAvailable ? "hazır" : "kurulamadı " + usbip)}.");
            }
            case UninstallArg:
            {
                var report = HMUninstall.Run(new HMUninstallOptions { IncludeUsbip = true });
                try { File.Delete(SelfTestFile); } catch { }
                string removed = string.Join(", ", report.Entries.Where(e => e.Outcome is HMUninstallOutcome.Removed or HMUninstallOutcome.RemovedAtRestart).Select(e => e.Kind).Distinct());
                string blocked = report.RunningPrograms.Count > 0 ? " Engelleyen programlar: " + string.Join(", ", report.RunningPrograms) : "";
                int code = report.Status switch { HMUninstallStatus.Removed => 0, HMUninstallStatus.RemovedRestartPending => 3, HMUninstallStatus.Blocked => 2, _ => 1 };
                return (code, $"Kaldırma: {report.Status}. Kaldırılan: {(removed.Length > 0 ? removed : "yok")}.{blocked}");
            }
            case SelfTestArg:
                return SelfTest();
        }
        return (1, "Bilinmeyen komut " + arg);
#else
        return (1, "HIDMaestro SDK bu derlemede yok.");
#endif
    }

#if HAS_HIDMAESTRO
    /// <summary>
    /// Creates a virtual DualSense (USB composite: HID + 4-ch audio), checks that Windows exposes its
    /// audio endpoint, then removes it. Proves install + transport + device creation end to end.
    /// </summary>
    private static (int, string) SelfTest()
    {
        var sw = Stopwatch.StartNew();
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        if (!ctx.IsDriverInstalled) return Save(false, "Sürücü kurulu değil.");
        if (!HMContext.IsUsbipBackendAvailable) return Save(false, "USB/IP arka ucu yok.");
        var profile = ctx.GetProfile(DualSenseCompositeProfile);
        if (profile == null) return Save(false, "Profil bulunamadı: " + DualSenseCompositeProfile);
        int before = CountWirelessControllerEndpoints();
        string audioInfo = "ses arabirimi yok";
        bool endpoint = false;
        using (var ctrl = ctx.CreateController(profile, "rw3pro-selftest"))
        {
            var state = new HMGamepadState();
            ctrl.SubmitState(in state);
            var audio = ctrl.UsbAudio;
            if (audio?.Output != null)
                audioInfo = $"ses {audio.Output.Channels} kanal/{audio.Output.SampleRateHz} Hz ({string.Join(",", audio.Output.ChannelRoles)})";
            for (int i = 0; i < 40 && !endpoint; i++) { Thread.Sleep(250); endpoint = CountWirelessControllerEndpoints() > before; }
        }
        bool ok = audioInfo.StartsWith("ses ") && endpoint;
        return Save(ok, $"Sanal DualSense oluşturuldu ve kaldırıldı; {audioInfo}; Windows ses cihazı {(endpoint ? "göründü" : "görünmedi")} ({sw.ElapsedMilliseconds} ms).");
    }

    private static (int, string) Save(bool ok, string summary)
    {
        try { Directory.CreateDirectory(StateDir); File.WriteAllText(SelfTestFile, JsonSerializer.Serialize(new SelfTestResult(DateTime.Now, ok, summary))); } catch { }
        return (ok ? 0 : 2, (ok ? "Sağlıklı: " : "Sorunlu: ") + summary);
    }

    private static int CountWirelessControllerEndpoints()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Count(d => d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }
#endif
}
