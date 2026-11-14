using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>Which sound becomes vibration in audio-to-haptics (audio mode and the native "Oyun sesinden" source).</summary>
public enum AudioCaptureScope
{
    /// <summary>Everything the default playback device plays (WASAPI loopback). Background music, video and voice chat too.</summary>
    AllSystem,
    /// <summary>Only the app in the foreground (the game while playing) and its child processes: Windows process
    /// loopback (Windows 10 2004+). Background YouTube, Discord or music then do not vibrate.</summary>
    ForegroundApp
}

/// <summary>Pure decisions for <see cref="AudioCaptureScope.ForegroundApp"/> (contract-tested).</summary>
public static class ForegroundAudioTarget
{
    /// <summary>Shell surfaces that take the foreground briefly (taskbar, Start, search, alt-tab, lock screen, IME).
    /// Focusing them keeps the previous target, so opening Start during a game does not cut the game's vibration.</summary>
    public static readonly IReadOnlySet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp",
        "TextInputHost", "ApplicationFrameHost", "dwm", "ShellHost", "Widgets", "WidgetBoard"
    };

    /// <summary>Process loopback exists from Windows 10 2004 (build 19041).</summary>
    public const int MinimumBuild = 19041;

    public static bool Supported(int windowsBuild) => windowsBuild >= MinimumBuild;

    /// <summary>Should focus on this process change the capture target? Not for RW3 itself (its window is in front
    /// while the user adjusts the slider) and not for shell surfaces; then the previous target stays.</summary>
    public static bool IsCandidate(string? processName, int processId, int ownProcessId) =>
        processId > 0 && processId != ownProcessId && !string.IsNullOrWhiteSpace(processName) && !ShellProcesses.Contains(processName);

    /// <summary>The target to capture after a foreground check: the new candidate, else the previous one.</summary>
    public static int Next(int previousTarget, string? foregroundName, int foregroundId, int ownProcessId) =>
        IsCandidate(foregroundName, foregroundId, ownProcessId) ? foregroundId : previousTarget;
}
