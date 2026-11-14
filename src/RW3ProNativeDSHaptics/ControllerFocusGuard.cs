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

    /// <summary>Pure decision (contract-tested): a known name, or an Electron / Chromium / CEF build next to the exe.</summary>
    public static bool IsTaker(string? exeName, string? exeDirectory, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        if (KnownTakers.Contains(Path.GetFileNameWithoutExtension(exeName))) return true;
        if (string.IsNullOrEmpty(exeDirectory)) return false;
        return fileExists(Path.Combine(exeDirectory, "resources", "app.asar"))
            || fileExists(Path.Combine(exeDirectory, "chrome_elf.dll"))
            || fileExists(Path.Combine(exeDirectory, "libcef.dll"));
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
