using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace RW3ProNativeDSHaptics;

public sealed record HidHideStatus(
    bool Installed, string Version, string? CliPath, bool FiltersHid, bool FiltersXbox,
    bool? CloakOn, bool AppRegistered, int PadNodes, int PadNodesHidden, bool RestorePending, string? Error)
{
    public bool Ready => Installed && CliPath != null && FiltersHid && FiltersXbox;
}

/// <summary>
/// HidHide (Nefarius, already used by DSX) hides the physical Xbox pad from games while the virtual
/// DualSense runs, so the game gets one controller instead of two. RW3 registers itself as an allowed
/// app (it still reads the pad through XInput and writes Sensa frames), hides only the pad's game
/// nodes (XInput/XnaComposite and the DirectInput "IG_" HID collection, never the Sensa control
/// collection), and restores the previous HidHide configuration when the bridge stops. Hiding applies
/// to new opens: a game that is already running must be restarted once. The previous
/// state is written to disk first, so a crash can be undone on the next start.
/// Writes need administrator rights (the bridge already runs elevated).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class HidHideControl
{
    public const string DownloadUrl = "https://github.com/nefarius/HidHide/releases/latest";
    private const string HidClassGuid = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
    private const string XnaCompositeGuid = "{d61ca365-5af4-4486-998b-9db4734c6ca3}";
    private const string XboxCompositeGuid = "{05f5cfe2-4733-4950-a6bb-07aad01a3a84}";
    private static string RestoreFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RW3ProNativeDSHaptics", "hidhide-restore.json");

    private sealed record RestoreState(bool CloakWasOn, List<string> AddedHidden, List<string> RemovedHidden);

    // ── pure helpers (contract-tested) ──

    /// <summary>Parses HidHideCLI "--dev-list" / "--app-list" output: one <c>--flag "value"</c> per line.</summary>
    public static List<string> ParseQuoted(string output, string flag) =>
        output.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith(flag + " \"", StringComparison.OrdinalIgnoreCase) && l.EndsWith('"'))
            .Select(l => l[(flag.Length + 2)..^1]).ToList();

    /// <summary>Is this device instance one of the pad's game-facing nodes? XInput (USB …&amp;MI_00 under
    /// XnaComposite/XboxComposite) and the DirectInput twin (HID\…&amp;IG_xx). Not the Razer control
    /// collections (Sensa), not other vendors' pads.</summary>
    public static bool IsPadGameNode(string instanceId, string? classGuid, string vidPid = "VID_1532&")
    {
        if (!instanceId.Contains(vidPid, StringComparison.OrdinalIgnoreCase)) return false;
        if (instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
            return instanceId.Contains("&IG_", StringComparison.OrdinalIgnoreCase);
        return classGuid != null && (classGuid.Equals(XnaCompositeGuid, StringComparison.OrdinalIgnoreCase)
                                     || classGuid.Equals(XboxCompositeGuid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"\\?\hid#vid_054c&amp;pid_0ce6&amp;mi_03#4&amp;bcf55c7&amp;0&amp;0000#{guid}" → "HID\VID_054C&amp;PID_0CE6&amp;MI_03\4&amp;BCF55C7&amp;0&amp;0000".</summary>
    public static string? InstanceFromHidPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        var parts = p.Split('#');
        return parts.Length >= 3 ? (parts[0] + "\\" + parts[1] + "\\" + parts[2]).ToUpperInvariant() : null;
    }

    // ── status ──

    public static string? FindCli()
    {
        var dirs = new List<string>();
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            foreach (var n in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(n);
                if ((s?.GetValue("DisplayName") as string)?.StartsWith("HidHide", StringComparison.OrdinalIgnoreCase) == true &&
                    s!.GetValue("InstallLocation") is string loc) dirs.Add(loc);
            }
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide"));
        return dirs.Select(d => Path.Combine(d, "x64", "HidHideCLI.exe")).FirstOrDefault(File.Exists);
    }

    private static string InstalledVersion()
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            foreach (var n in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(n);
                if ((s?.GetValue("DisplayName") as string)?.StartsWith("HidHide", StringComparison.OrdinalIgnoreCase) == true)
                    return s!.GetValue("DisplayVersion") as string ?? "?";
            }
        }
        return "?";
    }

    private static bool ClassFiltered(string guid)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + guid);
        return k?.GetValue("UpperFilters") is string[] f && f.Any(x => x.Equals("HidHide", StringComparison.OrdinalIgnoreCase));
    }

    public static HidHideStatus Query()
    {
        bool installed;
        using (var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide")) installed = svc != null;
        var cli = FindCli();
        bool fh = ClassFiltered(HidClassGuid), fx = ClassFiltered(XnaCompositeGuid);
        if (!installed || cli == null)
            return new HidHideStatus(installed, installed ? InstalledVersion() : "-", cli, fh, fx, null, false, 0, 0, File.Exists(RestoreFile), null);
        try
        {
            var cloak = Run(cli, "--cloak-state");
            var hidden = ParseQuoted(Run(cli, "--dev-list"), "--dev-hide");
            var apps = ParseQuoted(Run(cli, "--app-list"), "--app-reg");
            var pads = PadGameNodes();
            bool registered = Environment.ProcessPath is { } exe && apps.Any(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));
            return new HidHideStatus(true, InstalledVersion(), cli, fh, fx, cloak.Contains("--cloak-on"), registered,
                pads.Count, pads.Count(p => hidden.Contains(p, StringComparer.OrdinalIgnoreCase)), File.Exists(RestoreFile), null);
        }
        catch (Exception ex)
        {
            return new HidHideStatus(true, InstalledVersion(), cli, fh, fx, null, false, 0, 0, File.Exists(RestoreFile), ex.Message);
        }
    }

    // ── apply / restore (administrator) ──

    /// <summary>Hides the pad's game nodes and turns cloaking on. Returns a short result text.</summary>
    public static string HidePad(string? virtualHidPath)
    {
        var cli = FindCli() ?? throw new InvalidOperationException("HidHide kurulu değil");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe yolu bilinmiyor");
        if (File.Exists(RestoreFile)) Restore();   // never stack two runs on top of each other

        bool cloakWasOn = Run(cli, "--cloak-state").Contains("--cloak-on");
        var hidden = ParseQuoted(Run(cli, "--dev-list"), "--dev-hide");
        var pads = PadGameNodes();
        if (pads.Count == 0) return "Xbox kolu (Wolverine) bulunamadı; gizlenmedi.";
        var add = pads.Where(p => !hidden.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        // A hidden entry that happens to be our virtual DualSense would hide it from the game.
        var virt = InstanceFromHidPath(virtualHidPath);
        var remove = virt != null ? hidden.Where(h => h.Equals(virt, StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();

        Directory.CreateDirectory(Path.GetDirectoryName(RestoreFile)!);
        File.WriteAllText(RestoreFile, JsonSerializer.Serialize(new RestoreState(cloakWasOn, add, remove)));

        var args = new List<string> { "--app-reg", Quote(exe) };
        foreach (var p in add) { args.Add("--dev-hide"); args.Add(Quote(p)); }
        foreach (var r in remove) { args.Add("--dev-unhide"); args.Add(Quote(r)); }
        args.Add("--cloak-on");
        Run(cli, string.Join(' ', args));
        // No device restart: pnputil /restart-device blocks while the game (and our own XInput) hold the
        // pad open (hung 2026-11-08). HidHide applies to new opens, so a game already running keeps
        // seeing the pad until it is restarted.
        return $"Xbox kolu oyundan gizlendi ({pads.Count} düğüm; açık oyun varsa onu yeniden başlat).";
    }

    /// <summary>Puts HidHide back the way it was before <see cref="HidePad"/>. Safe to call when nothing is pending.</summary>
    public static string Restore()
    {
        if (!File.Exists(RestoreFile)) return "Geri alınacak bir şey yok.";
        var cli = FindCli() ?? throw new InvalidOperationException("HidHide kurulu değil");
        var st = JsonSerializer.Deserialize<RestoreState>(File.ReadAllText(RestoreFile)) ?? new RestoreState(false, new(), new());
        var args = new List<string>();
        foreach (var p in st.AddedHidden) { args.Add("--dev-unhide"); args.Add(Quote(p)); }
        foreach (var r in st.RemovedHidden) { args.Add("--dev-hide"); args.Add(Quote(r)); }
        args.Add(st.CloakWasOn ? "--cloak-on" : "--cloak-off");
        Run(cli, string.Join(' ', args));
        File.Delete(RestoreFile);
        return "HidHide eski hâline döndü.";
    }

    public static bool RestorePending => File.Exists(RestoreFile);

    private static string Quote(string s) => "\"" + s + "\"";

    private static string Run(string cli, string args)
    {
        var psi = new ProcessStartInfo(cli, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("HidHideCLI başlatılamadı");
        p.StandardInput.Close();   // the CLI waits on stdin when it has none (hung the first probe)
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } throw new TimeoutException("HidHideCLI yanıt vermedi"); }
        string o = outTask.Result + errTask.Result;
        // The CLI exits 0 even on errors ("The command is not recognized.", access denied): read the text.
        if (p.ExitCode != 0 || LooksLikeError(o)) throw new InvalidOperationException("HidHideCLI " + args.Split(' ')[0] + " → " + p.ExitCode + ": " + o.Trim());
        return o;
    }

    public static bool LooksLikeError(string output) =>
        new[] { "not recognized", "denied", "error", "failed", "invalid", "hata", "reddedildi" }
            .Any(w => output.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ── device enumeration (SetupAPI, present devices of all classes) ──

    public static List<string> PadGameNodes()
    {
        var r = new List<string>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);   // PRESENT | ALLCLASSES
        if (set == new IntPtr(-1)) return r;
        try
        {
            var info = new DevInfo { Size = Marshal.SizeOf<DevInfo>() };
            var sb = new System.Text.StringBuilder(512);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                sb.Clear();
                if (!SetupDiGetDeviceInstanceId(set, ref info, sb, sb.Capacity, out _)) continue;
                string id = sb.ToString();
                if (IsPadGameNode(id, info.ClassGuid.ToString("B"))) r.Add(id);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return r;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DevInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr cls, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfo info, System.Text.StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
