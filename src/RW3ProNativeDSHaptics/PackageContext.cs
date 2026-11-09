using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// RW3 is a plain desktop app. Started from a packaged (MSIX) app such as the Claude or Codex desktop
/// apps, it inherits that package's identity, and Windows silently redirects its writes under
/// %LOCALAPPDATA% (settings, log, HidHide restore file) and HKCU (autostart) into that package's
/// private store. Found 2026-11-09: three diverging copies of settings.json and rw3.log (real folder,
/// Packages\Claude_…\LocalCache, Packages\OpenAI.Codex_…\LocalCache). On such a start RW3 relaunches
/// itself through explorer.exe, which starts it as an ordinary desktop process, and exits.
/// </summary>
public static class PackageContext
{
    public const int NoPackage = 15700;              // APPMODEL_ERROR_NO_PACKAGE
    private const int InsufficientBuffer = 122;
    public const string NoBreakawayArg = "--no-breakaway";
    /// <summary>A second relaunch within this window means explorer did not break away: stop, don't loop.</summary>
    public static readonly TimeSpan LoopGuard = TimeSpan.FromSeconds(20);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    /// <summary>Full name of the package this process runs in, or null for an ordinary desktop process.</summary>
    public static string? CurrentPackage()
    {
        try
        {
            int len = 0;
            int r = GetCurrentPackageFullName(ref len, null);
            if (r == NoPackage || r != InsufficientBuffer || len <= 0) return null;
            var sb = new StringBuilder(len);
            return GetCurrentPackageFullName(ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }   // API absent: treat as unpackaged
    }

    /// <summary>Relaunch only for a plain start (explorer cannot pass arguments) inside SOMEONE ELSE's package,
    /// and not again right after a relaunch attempt (loop guard).</summary>
    public static bool ShouldBreakAway(string? package, IReadOnlyList<string> args, DateTime? lastAttemptUtc, DateTime nowUtc) =>
        package != null
        && !package.StartsWith("RW3", StringComparison.OrdinalIgnoreCase)
        && args.Count == 0
        && (lastAttemptUtc == null || nowUtc - lastAttemptUtc.Value > LoopGuard || nowUtc < lastAttemptUtc.Value);

    /// <summary>
    /// API-independent check (a sandboxed launcher may not expose a package identity): write a one-off
    /// token under %LOCALAPPDATA%\RW3ProNativeDSHaptics and look for it in every package's redirect store
    /// (%LOCALAPPDATA%\Packages\*\LocalCache\Local\RW3ProNativeDSHaptics). Found there = our writes are
    /// redirected; returns that package folder name.
    /// </summary>
    public static string? DetectRedirectedWrites()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(local, "RW3ProNativeDSHaptics");
        string name = ".redirect-probe-" + Environment.ProcessId + ".txt";
        string token = Guid.NewGuid().ToString("N");
        string probe = Path.Combine(dir, name);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(probe, token);
            string packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (var pkg in Directory.EnumerateDirectories(packages))
            {
                string candidate = Path.Combine(pkg, "LocalCache", "Local", "RW3ProNativeDSHaptics", name);
                try { if (File.Exists(candidate) && File.ReadAllText(candidate) == token) return Path.GetFileName(pkg); }
                catch { }
            }
            return null;
        }
        catch { return null; }
        finally { try { File.Delete(probe); } catch { } }
    }

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "breakaway-attempt.txt");

    /// <summary>Called first thing at startup. True = a clean copy was started; this process must exit.</summary>
    public static bool RelaunchOutsideForeignPackage(IReadOnlyList<string> args, Action<string>? log = null)
    {
        string? package = CurrentPackage() ?? DetectRedirectedWrites();
        if (package == null) return false;
        DateTime? last = null;
        try { if (File.Exists(MarkerPath)) last = File.GetLastWriteTimeUtc(MarkerPath); } catch { }
        if (!ShouldBreakAway(package, args, last, DateTime.UtcNow))
        {
            log?.Invoke("Running inside package '" + package + "': settings and log are redirected into that package's store" +
                (args.Count > 0 ? " (started with arguments; no relaunch)." : " (relaunch did not break away; continuing)."));
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, package);
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe path unknown");
            Process.Start(new ProcessStartInfo(explorer, "\"" + exe + "\"") { UseShellExecute = false });
            log?.Invoke("Started inside package '" + package + "': relaunched through explorer.exe as a plain desktop app.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("Package breakaway failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }
}
