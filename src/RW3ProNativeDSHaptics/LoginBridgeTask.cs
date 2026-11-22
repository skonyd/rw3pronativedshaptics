using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// The boot task that runs <see cref="LoginBridge"/> as SYSTEM (docs/WINUSB_DIRECT_MODE.md, "Sign-in screen").
/// <list type="bullet">
/// <item>The bridge runs from its own copy under Program Files, which only administrators can change. RW3 itself lives in
/// the user's profile, which the user (and anything the user runs) can write: a SYSTEM task must not start an exe from
/// there.</item>
/// <item>RW3 refreshes that copy when it starts elevated and finds it older than itself.</item>
/// <item>Stopping asks the bridge to stop (Global stop event, the virtual pad closes cleanly) before ending the task.</item>
/// </list>
/// Installing, refreshing and removing need administrator rights (RW3 has them while direct mode is in use).
/// </summary>
public static class LoginBridgeTask
{
    public const string TaskName = "RW3ProNativeDSHaptics LoginBridge";
    public const string StopName = @"Global\RW3ProNativeDSHaptics.LoginBridge.Stop";
    private const string ExeName = "RW3ProNativeDSHaptics.exe", MainDll = "RW3ProNativeDSHaptics.dll";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RW3ProNativeDSHaptics", "LoginBridge");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    /// <summary>True when the task exists and runs the Program Files copy.</summary>
    public static bool IsInstalled()
    {
        var (code, xml) = ElevatedLaunchTask.Schtasks("/query /tn \"" + TaskName + "\" /xml");
        return code == 0 && ElevatedLaunchTask.PointsAt(xml, InstalledExe);
    }

    /// <summary>Task definition (pure; the contract tests check the security-relevant parts): SYSTEM, at boot, no time
    /// limit, restarted if it fails, runs the Program Files copy with <see cref="LoginBridge.Arg"/>.</summary>
    public static string BuildXml(string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>RW3 Pro Haptics: doğrudan moddaki Wolverine'i Windows giriş ekranında da çalıştırır (sanal Xbox kolu).</Description></RegistrationInfo>
          <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>{LoginBridge.Arg}</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Copies RW3 to Program Files, (re)creates the task and starts it.</summary>
    public static (bool Ok, string Message) Install()
    {
        try
        {
            Stop();
            CopyApp();
            string file = Path.Combine(Path.GetTempPath(), "rw3-login-bridge-task.xml");
            File.WriteAllText(file, BuildXml(InstalledExe), Encoding.Unicode);
            try
            {
                var (code, output) = ElevatedLaunchTask.Schtasks("/create /tn \"" + TaskName + "\" /xml \"" + file + "\" /f");
                if (code != 0) return (false, "Görev oluşturulamadı: " + output.Trim());
            }
            finally { try { File.Delete(file); } catch { } }
            ElevatedLaunchTask.Schtasks("/run /tn \"" + TaskName + "\"");
            DiagnosticsService.Info("Login bridge task installed: " + InstalledExe);
            return (true, "Giriş ekranı köprüsü kuruldu; Windows açılışında kendiliğinden başlar.");
        }
        catch (Exception ex) { return (false, "Kurulamadı: " + ex.Message); }
    }

    /// <summary>Stops the bridge, deletes the task and the Program Files copy.</summary>
    public static (bool Ok, string Message) Uninstall()
    {
        Stop();
        var (code, output) = ElevatedLaunchTask.Schtasks("/delete /tn \"" + TaskName + "\" /f");
        bool missing = code != 0 && output.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); }
        catch (Exception ex) { DiagnosticsService.Info("Login bridge: copy not deleted (" + ex.Message + ")."); }
        DiagnosticsService.Info("Login bridge task removed (schtasks " + code + ").");
        return code == 0 || missing ? (true, "Giriş ekranı köprüsü kaldırıldı.") : (false, "Görev silinemedi: " + output.Trim());
    }

    /// <summary>RW3 started elevated: when the installed copy differs from this build, stop the bridge, copy, restart it.</summary>
    public static void RefreshIfStale()
    {
        try
        {
            if (!Environment.IsPrivilegedProcess || !IsInstalled()) return;
            string mine = Path.Combine(AppContext.BaseDirectory, MainDll), theirs = Path.Combine(InstallDir, MainDll);
            if (File.Exists(theirs) && SameFile(mine, theirs)) return;
            DiagnosticsService.Info("Login bridge: copy is older than this RW3, refreshing.");
            Stop();
            CopyApp();
            ElevatedLaunchTask.Schtasks("/run /tn \"" + TaskName + "\"");
        }
        catch (Exception ex) { DiagnosticsService.Info("Login bridge refresh failed: " + ex.Message); }
    }

    /// <summary>Asks a running bridge to stop (it closes its virtual pad), waits up to 6 s, then ends the task.</summary>
    private static void Stop()
    {
        IntPtr stop = CreateStopEvent();
        if (stop != IntPtr.Zero) { DirectModeOwnership.SetEvent(stop); }
        for (int i = 0; i < 30 && IsRunning(); i++) Thread.Sleep(200);
        ElevatedLaunchTask.Schtasks("/end /tn \"" + TaskName + "\"");
        if (stop != IntPtr.Zero) { DirectModeOwnership.ResetEvent(stop); DirectModeOwnership.CloseHandle(stop); }
    }

    private static bool IsRunning()
    {
        var (code, output) = ElevatedLaunchTask.Schtasks("/query /tn \"" + TaskName + "\" /fo csv /nh");
        return code == 0 && (output.Contains("Running", StringComparison.OrdinalIgnoreCase) || output.Contains("Çalışıyor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The bridge's stop event (manual reset, same SYSTEM/administrators-only DACL as the owner objects).</summary>
    public static IntPtr CreateStopEvent() => DirectModeOwnership.CreateNamedEvent(StopName, manualReset: true);

    private static void CopyApp()
    {
        string src = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(InstallDir);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith("TestRange" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // the test game is not needed at boot
            string dst = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(file, dst, overwrite: true); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(300); }   // the stopping bridge may still hold it
            }
        }
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        using var ha = File.OpenRead(a); using var hb = File.OpenRead(b);
        return SHA256.HashData(ha).AsSpan().SequenceEqual(SHA256.HashData(hb));
    }
}
