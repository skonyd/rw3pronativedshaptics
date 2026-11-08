using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Device identity means a currently-present USB PnP node with an expected
/// vendor+product identifier. It is NOT a haptic readback or proof of a
/// direct cable: Razer's wireless receiver can expose the same VID/PID.
/// </summary>
public sealed record UsbGamepadPresence(
    bool DualSenseUsbNode, bool WolverineUsbNode,
    int DualSenseNodes, int RazerNodes,
    bool ScanSucceeded, string Diagnostic, bool WolverineWiredPcNode = false)
{
    public static UsbGamepadPresence Unknown(string error) =>
        new(false, false, 0, 0, false, error);
}

public static class UsbGamepadClassifier
{
    public static bool IsDualSenseUsbNode(string? pnpId) =>
        IsUsbVidPid(pnpId, "054C", "0CE6") ||
        IsUsbVidPid(pnpId, "054C", "0DF2");

    // Observed on real hardware (2026-11-07, Windows 11): the HyperSpeed dongle and the
    // controller in Xbox mode enumerate as 1532:0A3F; the controller on a USB cable in
    // PC mode enumerates as 1532:0A4C ("Razer Wolverine V3 Pro for Xbox", with a MEDIA
    // interface). Only 0A4C proves a wired PC-mode connection.
    public static bool IsWolverineV3ProUsbNode(string? pnpId) =>
        IsUsbVidPid(pnpId, "1532", "0A3F") || IsWolverineWiredPcModeNode(pnpId);

    public static bool IsWolverineWiredPcModeNode(string? pnpId) =>
        IsUsbVidPid(pnpId, "1532", "0A4C");

    private static bool IsUsbVidPid(string? input, string vid, string pid)
    {
        if (string.IsNullOrEmpty(input)) return false;
        // Require USB enumerator root, not HID children, SWD/virtual-only or a
        // friendly-name match (which can be spoofed by virtual controllers).
        const string prefix = "USB\\VID_";
        if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string fullPrefix = prefix + vid + "&PID_" + pid;
        if (!input.StartsWith(fullPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return input.Length == fullPrefix.Length ||
            input[fullPrefix.Length] is '&' or '\\';
    }

    public static UsbGamepadPresence Classify(IEnumerable<string?> presentPnPIds)
    {
        if (presentPnPIds == null) return UsbGamepadPresence.Unknown("USB aygıt listesi bulunamadı");
        int sony = 0, razer = 0;
        bool wired = false;
        foreach (string? id in presentPnPIds)
        {
            if (IsDualSenseUsbNode(id)) ++sony;
            if (IsWolverineV3ProUsbNode(id)) ++razer;
            if (IsWolverineWiredPcModeNode(id)) wired = true;
        }
        return new(sony > 0, razer > 0, sony, razer, true,
            "USB PnP kimliği gözlendi; fiziksel haptics ayrı doğrulanmalıdır.", wired);
    }
}
