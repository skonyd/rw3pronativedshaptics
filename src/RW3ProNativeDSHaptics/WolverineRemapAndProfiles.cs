using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Firmware-verified Razer Wolverine V3 Pro multi-function button IDs (Dev2FW 0x34F79 / Class 0x02 Cmd 0x0C &amp; 0x84).
/// M1/M2 are the top claw bumpers; M3–M6 are the four rear mouse-click paddles.
/// </summary>
public enum MButtonId : byte
{
    M1 = 0x10, // Sol Üst Pençe (LB yanı)
    M2 = 0x11, // Sağ Üst Pençe (RB yanı)
    M3 = 0x12, // Sol Üst Arka Pedal
    M4 = 0x13, // Sağ Üst Arka Pedal
    M5 = 0x14, // Sol Alt Arka Pedal
    M6 = 0x15  // Sağ Alt Arka Pedal
}

/// <summary>
/// Firmware-verified target function codes for M1–M6 remapping (Dev2FW 0x0EA98, 0x1B2D0, and 0x1EEB0).
/// </summary>
public enum RemapTarget : byte
{
    Disabled = 0x00,
    A = 0x20,
    B = 0x21,
    X = 0x22,
    Y = 0x23,
    LB = 0x24,
    RB = 0x25,
    LT = 0x26,
    RT = 0x27,
    L3 = 0x28,
    R3 = 0x29,
    DpadUp = 0x2C,
    DpadDown = 0x2D,
    DpadLeft = 0x2E,
    DpadRight = 0x2F,
    View = 0x34,
    Menu = 0x35,
    SensitivityClutch = 0x37
}

public sealed class ControllerProfile
{
    public int Slot { get; set; } = 1; // 1..4 (matches onboard Wolverine V3 Pro profile slots)
    public string Name { get; set; } = "Profil 1";
    public RemapTarget M1 { get; set; } = RemapTarget.X;
    public RemapTarget M2 { get; set; } = RemapTarget.Y;
    public RemapTarget M3 { get; set; } = RemapTarget.A;
    public RemapTarget M4 { get; set; } = RemapTarget.B;
    public RemapTarget M5 { get; set; } = RemapTarget.SensitivityClutch;
    public RemapTarget M6 { get; set; } = RemapTarget.SensitivityClutch;

    public RemapTarget Get(MButtonId id) => id switch
    {
        MButtonId.M1 => M1,
        MButtonId.M2 => M2,
        MButtonId.M3 => M3,
        MButtonId.M4 => M4,
        MButtonId.M5 => M5,
        MButtonId.M6 => M6,
        _ => RemapTarget.Disabled
    };

    public void Set(MButtonId id, RemapTarget target)
    {
        switch (id)
        {
            case MButtonId.M1: M1 = target; break;
            case MButtonId.M2: M2 = target; break;
            case MButtonId.M3: M3 = target; break;
            case MButtonId.M4: M4 = target; break;
            case MButtonId.M5: M5 = target; break;
            case MButtonId.M6: M6 = target; break;
        }
    }
}

/// <summary>
/// Firmware-verified Razer Class 0x05 (Onboard Profiles 1–4, Dev2FW 0x1B894) and
/// Class 0x02 (M1–M6 Button Remapping, Dev2FW 0x17740 / 0x1EE34 / 0x0EA98) commands for Wolverine V3 Pro.
/// Works in both PC mode (HID Feature Report 4) and Xbox mode (GIP 0x0F tunnel).
/// </summary>
public static class WolverineRemapAndProfiles
{
    public const byte RemapClass = 0x02;
    public const byte CmdSetRemap = 0x0C;
    public const byte CmdGetRemap = 0x8C;

    public const byte ProfileClass = 0x05;
    public const byte CmdSetActiveProfile = 0x04;
    public const byte CmdGetActiveProfile = 0x84;

    private static readonly object ApplyGate = new();

    public static IReadOnlyList<MButtonId> AllMButtons { get; } =
        [MButtonId.M1, MButtonId.M2, MButtonId.M3, MButtonId.M4, MButtonId.M5, MButtonId.M6];

    public static IReadOnlyList<RemapTarget> AllTargets { get; } =
    [
        RemapTarget.A, RemapTarget.B, RemapTarget.X, RemapTarget.Y,
        RemapTarget.LB, RemapTarget.RB, RemapTarget.LT, RemapTarget.RT,
        RemapTarget.L3, RemapTarget.R3,
        RemapTarget.DpadUp, RemapTarget.DpadDown, RemapTarget.DpadLeft, RemapTarget.DpadRight,
        RemapTarget.View, RemapTarget.Menu,
        RemapTarget.SensitivityClutch, RemapTarget.Disabled
    ];

    public static string MButtonLabel(MButtonId id) => id switch
    {
        MButtonId.M1 => "M1 (Sol Üst Pençe)",
        MButtonId.M2 => "M2 (Sağ Üst Pençe)",
        MButtonId.M3 => "M3 (Sol Üst Arka Pedal)",
        MButtonId.M4 => "M4 (Sağ Üst Arka Pedal)",
        MButtonId.M5 => "M5 (Sol Alt Arka Pedal)",
        MButtonId.M6 => "M6 (Sağ Alt Arka Pedal)",
        _ => id.ToString()
    };

    public static string TargetShortLabel(RemapTarget target) => target switch
    {
        RemapTarget.Disabled => "Kapalı",
        RemapTarget.A => "Ⓐ",
        RemapTarget.B => "Ⓑ",
        RemapTarget.X => "Ⓧ",
        RemapTarget.Y => "Ⓨ",
        RemapTarget.LB => "LB",
        RemapTarget.RB => "RB",
        RemapTarget.LT => "LT",
        RemapTarget.RT => "RT",
        RemapTarget.L3 => "L3",
        RemapTarget.R3 => "R3",
        RemapTarget.DpadUp => "↑",
        RemapTarget.DpadDown => "↓",
        RemapTarget.DpadLeft => "←",
        RemapTarget.DpadRight => "→",
        RemapTarget.View => "⧉",
        RemapTarget.Menu => "☰",
        RemapTarget.SensitivityClutch => "Clutch",
        _ => target.ToString()
    };

    public static string TargetFullLabel(RemapTarget target) => target switch
    {
        RemapTarget.Disabled => "Devre Dışı (Boş)",
        RemapTarget.A => "Ⓐ  A Tuşu",
        RemapTarget.B => "Ⓑ  B Tuşu",
        RemapTarget.X => "Ⓧ  X Tuşu",
        RemapTarget.Y => "Ⓨ  Y Tuşu",
        RemapTarget.LB => "LB  Sol Tampon",
        RemapTarget.RB => "RB  Sağ Tampon",
        RemapTarget.LT => "LT  Sol Tetik",
        RemapTarget.RT => "RT  Sağ Tetik",
        RemapTarget.L3 => "L3  Sol Stick Bas",
        RemapTarget.R3 => "R3  Sağ Stick Bas",
        RemapTarget.DpadUp => "D-Pad ↑  Yukarı",
        RemapTarget.DpadDown => "D-Pad ↓  Aşağı",
        RemapTarget.DpadLeft => "D-Pad ←  Sol",
        RemapTarget.DpadRight => "D-Pad →  Sağ",
        RemapTarget.View => "⧉  View Tuşu",
        RemapTarget.Menu => "☰  Menü Tuşu",
        RemapTarget.SensitivityClutch => "🎯 Hassasiyet Düşür (Clutch)",
        _ => target.ToString()
    };

    public static List<ControllerProfile> CreateDefaultProfiles() =>
    [
        new ControllerProfile
        {
            Slot = 1, Name = "Profil 1 · Genel",
            M1 = RemapTarget.X, M2 = RemapTarget.Y,
            M3 = RemapTarget.A, M4 = RemapTarget.B,
            M5 = RemapTarget.SensitivityClutch, M6 = RemapTarget.SensitivityClutch
        },
        new ControllerProfile
        {
            Slot = 2, Name = "Profil 2 · FPS / Nişan",
            M1 = RemapTarget.X, M2 = RemapTarget.Y,
            M3 = RemapTarget.A, M4 = RemapTarget.B,
            M5 = RemapTarget.L3, M6 = RemapTarget.SensitivityClutch
        },
        new ControllerProfile
        {
            Slot = 3, Name = "Profil 3 · Aksiyon / RPG",
            M1 = RemapTarget.LB, M2 = RemapTarget.RB,
            M3 = RemapTarget.B, M4 = RemapTarget.A,
            M5 = RemapTarget.DpadUp, M6 = RemapTarget.DpadDown
        },
        new ControllerProfile
        {
            Slot = 4, Name = "Profil 4 · Özel",
            M1 = RemapTarget.Disabled, M2 = RemapTarget.Disabled,
            M3 = RemapTarget.A, M4 = RemapTarget.B,
            M5 = RemapTarget.X, M6 = RemapTarget.Y
        }
    ];

    /// <summary>Builds Class 0x05 Cmd 0x04 (Set Active Profile) payload: [profileSlot 1..4].</summary>
    public static byte[] BuildSetProfileArgs(byte profileSlot)
    {
        if (profileSlot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(profileSlot));
        return [profileSlot];
    }

    /// <summary>
    /// Builds the 10-byte Class 0x02 Cmd 0x0C (Set M1–M6 Button Mapping) payload verified against Dev2FW 0x17784 &amp; 0x17934:
    /// [0] profile (0 = active RAM, 1..4 = saved profile), [1] buttonId (0x10..0x15), [2] layer (0 = normal),
    /// [3] funcType (0x00 = Disabled, 0x10 = Gamepad/Clutch), [4] subMode (0x01 = Button, 0x05 = Clutch),
    /// [5] targetCode (0x20..0x35 or 0x37), [6..9] reserved 0x00.
    /// </summary>
    public static byte[] BuildSetRemapArgs(byte profile, MButtonId button, RemapTarget target)
    {
        if (profile > 4) throw new ArgumentOutOfRangeException(nameof(profile));
        if (target == RemapTarget.Disabled)
            return [profile, (byte)button, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        if (target == RemapTarget.SensitivityClutch)
            return [profile, (byte)button, 0x00, 0x10, 0x05, (byte)RemapTarget.SensitivityClutch, 0x00, 0x00, 0x00, 0x00];
        return [profile, (byte)button, 0x00, 0x10, 0x01, (byte)target, 0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>Switches the active onboard profile (1–4) on the controller.</summary>
    public static (bool Ok, string Message) ApplyProfileSwitch(int profileSlot)
    {
        byte slot = (byte)Math.Clamp(profileSlot, 1, 4);
        lock (ApplyGate)
        {
            return WolverineLightingAndBattery.SendCommand(ProfileClass, CmdSetActiveProfile, BuildSetProfileArgs(slot));
        }
    }

    /// <summary>Applies a single M1–M6 mapping immediately to active RAM (0) and saves it into the specified profile slot (1–4).</summary>
    public static (bool Ok, string Message) ApplyButtonRemap(int profileSlot, MButtonId button, RemapTarget target)
    {
        byte slot = (byte)Math.Clamp(profileSlot, 1, 4);
        lock (ApplyGate)
        {
            var (okRam, msgRam) = WolverineLightingAndBattery.SendCommand(RemapClass, CmdSetRemap, BuildSetRemapArgs(0, button, target));
            if (!okRam) return (false, msgRam);
            System.Threading.Thread.Sleep(45);
            var (okSlot, msgSlot) = WolverineLightingAndBattery.SendCommand(RemapClass, CmdSetRemap, BuildSetRemapArgs(slot, button, target));
            return (okSlot, okSlot ? $"{MButtonLabel(button)} → {TargetShortLabel(target)} (Profil {slot}, {msgSlot})" : msgSlot);
        }
    }

    /// <summary>Switches to the profile's onboard slot and pushes all 6 M1–M6 mappings to the controller.</summary>
    public static (bool Ok, string Message) ApplyFullProfile(ControllerProfile profile)
    {
        byte slot = (byte)Math.Clamp(profile.Slot, 1, 4);
        lock (ApplyGate)
        {
            var (okSw, msgSw) = WolverineLightingAndBattery.SendCommand(ProfileClass, CmdSetActiveProfile, BuildSetProfileArgs(slot));
            if (!okSw) return (false, msgSw);
            System.Threading.Thread.Sleep(55);

            foreach (var btn in AllMButtons)
            {
                var target = profile.Get(btn);
                var (okR, msgR) = WolverineLightingAndBattery.SendCommand(RemapClass, CmdSetRemap, BuildSetRemapArgs(0, btn, target));
                if (!okR) return (false, msgR);
                System.Threading.Thread.Sleep(40);
                var (okS, msgS) = WolverineLightingAndBattery.SendCommand(RemapClass, CmdSetRemap, BuildSetRemapArgs(slot, btn, target));
                if (!okS) return (false, msgS);
                System.Threading.Thread.Sleep(40);
            }
            return (true, $"{profile.Name} (M1–M6) kola yazıldı ({msgSw}).");
        }
    }
}
