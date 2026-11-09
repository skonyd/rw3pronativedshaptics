using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// UAC-free start of the virtual DualSense bridge. The bridge needs administrator rights (HIDMaestro,
/// HidHide); asking UAC and restarting the app on every start was the biggest everyday friction.
/// The user enables this once (one UAC prompt): a per-user scheduled task "run with highest privileges"
/// that starts THIS exe with <c>--start-virtual-dualsense</c>. Starting the task needs no prompt.
/// Normal process priority (task default 7 = below normal would hurt the 33 ms haptic cadence).
/// The task is used only while it points at the running exe; another build re-offers setup.
/// </summary>
public static class ElevatedLaunchTask
{
    public const string TaskName = "RW3ProNativeDSHaptics Bridge";
    public const string InstallArg = "--bridge-task-install", UninstallArg = "--bridge-task-uninstall";
    /// <summary>Argument the task passes (App.StartVirtualDualSenseArg is defined from this).</summary>
    public const string BridgeArg = "--start-virtual-dualsense";

    public enum TaskState { Missing, Ready, OtherExe }

    public static TaskState Query(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        var (code, xml) = Schtasks("/query /tn \"" + TaskName + "\" /xml");
        if (code != 0 || string.IsNullOrWhiteSpace(xml)) return TaskState.Missing;
        return exePath != null && PointsAt(xml, exePath) ? TaskState.Ready : TaskState.OtherExe;
    }

    /// <summary>True when the task XML runs exactly this exe (XML-escaped, case-insensitive).</summary>
    public static bool PointsAt(string taskXml, string exePath) =>
        taskXml.Contains("<Command>" + SecurityElement.Escape(exePath) + "</Command>", StringComparison.OrdinalIgnoreCase) ||
        taskXml.Contains("<Command>\"" + SecurityElement.Escape(exePath) + "\"</Command>", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts the elevated bridge without a prompt. False when the task is missing or for another exe.</summary>
    public static bool TryRun()
    {
        if (Query() != TaskState.Ready) return false;
        return Schtasks("/run /tn \"" + TaskName + "\"").Code == 0;
    }

    /// <summary>Task definition (pure; tests check the security-relevant parts).</summary>
    public static string BuildXml(string exePath, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>RW3 Pro Haptics: sanal DualSense köprüsünü UAC sormadan başlatır.</Description></RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>{BridgeArg}</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Elevated: creates/replaces the task for this exe and the interactive user.</summary>
    public static (int Code, string Message) Install()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe yolu bilinmiyor");
        string user = WindowsIdentity.GetCurrent().Name;
        string file = Path.Combine(Path.GetTempPath(), "rw3-bridge-task.xml");
        File.WriteAllText(file, BuildXml(exe, user), Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks("/create /tn \"" + TaskName + "\" /xml \"" + file + "\" /f");
            return code == 0 ? (0, "Köprü artık UAC sormadan başlar.") : (code, "Görev oluşturulamadı: " + output.Trim());
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Elevated: removes the task.</summary>
    public static (int Code, string Message) Uninstall()
    {
        var (code, output) = Schtasks("/delete /tn \"" + TaskName + "\" /f");
        return code == 0 ? (0, "UAC'siz başlatma kapatıldı.") : (code, "Görev silinemedi: " + output.Trim());
    }

    private static (int Code, string Output) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "schtasks başlatılamadı");
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "schtasks yanıt vermedi"); }
            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
