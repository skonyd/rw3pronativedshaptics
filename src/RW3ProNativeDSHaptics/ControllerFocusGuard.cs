using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Xbox mode: the palms (Razer GIP channel) follow Windows' controller focus. An app in front that opens the Xbox pad
/// itself takes the output and the palm frames are dropped silently (E-20261114-25/-30). Chromium browsers and
/// Electron/CEF apps (Opera, Chrome, Edge, Claude, Codex, Discord, Spotify…) do this through their gamepad support;
/// games through the virtual DualSense do not. Windows gives no feedback, so the foreground app is classified instead.
/// </summary>
public static class ControllerFocusGuard
{
    /// <summary>Executables known to open gamepads while in front (lower case, no extension).</summary>
    public static readonly IReadOnlySet<string> KnownTakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "opera", "opera_gx", "chrome", "msedge", "brave", "vivaldi", "firefox", "librewolf", "waterfox", "chromium",
        "claude", "codex", "chatgpt", "discord", "spotify", "steamwebhelper", "code", "slack", "ms-teams", "whatsapp", "obsidian", "notion"
    };

    /// <summary>User option (both audio panels): play the body motors while such an app blocks the palms. Off = the
    /// vibration simply stops meanwhile, so everything felt is Sensa.</summary>
    public static volatile bool FallbackEnabled = true;

    /// <summary>Pure decision (contract-tested): a known name, or an Electron app (resources/app.asar next to the exe).
    /// chrome_elf.dll / libcef.dll are not used: some games ship CEF for menus and would be mistaken for a browser.</summary>
    public static bool IsTaker(string? exeName, string? exeDirectory, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        if (KnownTakers.Contains(Path.GetFileNameWithoutExtension(exeName))) return true;
        if (string.IsNullOrEmpty(exeDirectory)) return false;
        return fileExists(Path.Combine(exeDirectory, "resources", "app.asar"));
    }

    private static int _cachedPid;
    private static bool _cachedTaker;

    /// <summary>True while another app that takes the controller output is in front (RW3 itself never counts).</summary>
    public static bool ForegroundTakesController()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
        if (pid == _cachedPid) return _cachedTaker;
        string? path = ImagePath(pid);
        bool taker = path != null && IsTaker(Path.GetFileName(path), Path.GetDirectoryName(path), File.Exists);
        _cachedPid = (int)pid; _cachedTaker = taker;
        return taker;
    }

    private static string? ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>
/// Games that already had a pad open when RW3 created a virtual pad. HidHide hides the Wolverine only from new opens,
/// so such a game keeps the real pad: its input, rumble and triggers bypass RW3, and in Xbox mode it holds the
/// controller focus (palms silent while it is in front). 2026-11-15: Banishers started before the Series pad kept
/// the real pad for the whole session and the user took it for the virtual pad working.
/// </summary>
public static class PadHolders
{
    private static readonly string[] PadModules = { "gameinput.dll", "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll", "xinputuap.dll" };
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "steam", "RW3ProNativeDSHaptics", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost" };

    /// <summary>Windowed processes started before <paramref name="before"/> that load a gamepad API (GameInput / XInput);
    /// browsers and Electron apps (ControllerFocusGuard.KnownTakers) are left out.</summary>
    public static List<(int Pid, string Name)> Find(DateTime before)
    {
        var found = new List<(int, string)>();
        int self = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || Ignore.Contains(p.ProcessName) ||
                    ControllerFocusGuard.KnownTakers.Contains(p.ProcessName) || p.StartTime >= before) continue;
                foreach (System.Diagnostics.ProcessModule m in p.Modules)
                    if (Array.Exists(PadModules, n => n.Equals(m.ModuleName, StringComparison.OrdinalIgnoreCase)))
                    {
                        string title = p.MainWindowTitle;
                        found.Add((p.Id, string.IsNullOrWhiteSpace(title) ? p.ProcessName : title));
                        break;
                    }
            }
            catch { }   // protected or exited process
            finally { p.Dispose(); }
        }
        return found;
    }

    public static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
