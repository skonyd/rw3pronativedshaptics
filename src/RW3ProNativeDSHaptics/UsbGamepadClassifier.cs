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

/// <summary>Which XInput slot is the Wolverine (the strict virtual pad reads only that one).</summary>
public static class XInputWolverineSlot
{
    /// <summary>
    /// Whether an XInput slot can be the Wolverine: Razer VID (PC mode), or 045E:02FF, the XInput child Windows gives a
    /// wired GIP pad (the Wolverine in Xbox mode, parent USB\VID_1532&amp;PID_0A3F, 2026-11-22). Unknown ids (0:0) may be it.
    /// Any other Microsoft pad is not: until 2026-11-22 a paired Bluetooth Xbox controller (045E:0B13) was read as the
    /// Wolverine after a replug, and its stick drift went to the game instead of the user's input.
    /// </summary>
    public static bool CanBe(ushort vid, ushort pid) => vid == 0x1532 || (vid == 0x045E && pid == 0x02FF) || (vid == 0 && pid == 0);

    /// <summary>The Wolverine among XInput slots that are not our virtual pad: the one Razer slot, else the one slot that
    /// can be it; -1 when none or ambiguous (never guess).</summary>
    public static int Choose(IReadOnlyList<(int Slot, ushort Vid, ushort Pid)> candidates)
    {
        var fit = new List<(int Slot, ushort Vid, ushort Pid)>();
        foreach (var c in candidates) if (CanBe(c.Vid, c.Pid)) fit.Add(c);
        if (fit.Count > 1) fit = fit.FindAll(c => c.Vid == 0x1532);
        return fit.Count == 1 ? fit[0].Slot : -1;
    }
}
