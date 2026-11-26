using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

public enum ShortcutTriggerKind
{
    ControllerCombo = 0,
    KeyboardHotkey = 1
}

public enum ShortcutAction
{
    CycleOutput = 0,
    OutputPhysical = 1,
    OutputXbox360 = 2,
    OutputXboxSeries = 3,
    OutputDualShock4 = 4,
    OutputDualSense = 5,
    TogglePalmAudio = 6,
    ToggleMelody = 7,
    CycleOutputLevel = 8,
    ToggleRgbPower = 9,
    CycleRgbEffect = 10,
    ToggleWindowVisibility = 11,
    VibrationLevelUp = 12,
    VibrationLevelDown = 13,
    RgbBrightnessUp = 14,
    RgbBrightnessDown = 15,
    SwitchToXboxMode = 16
}

public sealed class UserShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public ShortcutTriggerKind Kind { get; set; } = ShortcutTriggerKind.ControllerCombo;
    /// <summary>XInput button bitmask (e.g. ButtonView | ButtonDpadUp) when <see cref="Kind"/> is <see cref="ShortcutTriggerKind.ControllerCombo"/>.</summary>
    public ushort ControllerButtons { get; set; }
    /// <summary>Minimum hold duration in milliseconds for controller combos (0 = instant on press, e.g. 600 = hold 0.6s).</summary>
    public int HoldMs { get; set; }
    /// <summary>Win32 modifier flags (1=Alt, 2=Ctrl, 4=Shift, 8=Win) when <see cref="Kind"/> is <see cref="ShortcutTriggerKind.KeyboardHotkey"/>.</summary>
    public int KeyModifiers { get; set; }
    /// <summary>Virtual-Key code (VK_*) when <see cref="Kind"/> is <see cref="ShortcutTriggerKind.KeyboardHotkey"/>.</summary>
    public int VirtualKey { get; set; }
    public ShortcutAction Action { get; set; } = ShortcutAction.CycleOutput;
    public bool Enabled { get; set; } = true;

    public string TriggerDisplay => Kind == ShortcutTriggerKind.ControllerCombo
        ? ShortcutManager.FormatControllerCombo(ControllerButtons, HoldMs)
        : ShortcutManager.FormatKeyboardHotkey(KeyModifiers, VirtualKey);

    public string ActionDisplay => ShortcutManager.ActionLabel(Action);
    public string KindBadge => Kind == ShortcutTriggerKind.ControllerCombo ? "🎮 Kumanda" : "⌨ Klavye";
}

public sealed record BuiltInShortcutInfo(string Category, string Combo, string Description);

/// <summary>
/// Manages built-in shortcut reference information, custom user shortcuts (controller button combos + global keyboard hotkeys),
/// combo formatting, validation, and runtime edge/hold detection.
/// </summary>
public static class ShortcutManager
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    public const ushort ButtonDpadUp = 0x0001, ButtonDpadDown = 0x0002, ButtonDpadLeft = 0x0004, ButtonDpadRight = 0x0008,
        ButtonMenu = 0x0010, ButtonView = 0x0020, ButtonLeftThumb = 0x0040, ButtonRightThumb = 0x0080,
        ButtonLeftShoulder = 0x0100, ButtonRightShoulder = 0x0200,
        ButtonA = 0x1000, ButtonB = 0x2000, ButtonX = 0x4000, ButtonY = 0x8000;

    private static readonly (ushort Mask, string Label)[] ButtonNames =
    [
        (ButtonView, "⧉ View"),
        (ButtonMenu, "☰ Menü"),
        (ButtonLeftShoulder, "LB"),
        (ButtonRightShoulder, "RB"),
        (ButtonLeftThumb, "L3"),
        (ButtonRightThumb, "R3"),
        (ButtonDpadUp, "D-Pad ↑"),
        (ButtonDpadDown, "D-Pad ↓"),
        (ButtonDpadLeft, "D-Pad ←"),
        (ButtonDpadRight, "D-Pad →"),
        (ButtonA, "Ⓐ"),
        (ButtonB, "Ⓑ"),
        (ButtonX, "Ⓧ"),
        (ButtonY, "Ⓨ")
    ];

    public static IReadOnlyList<BuiltInShortcutInfo> BuiltInShortcuts { get; } =
    [
        new("Titreşim (Her Yerde)", "Fn + LB / RB",
            "Kolun dahili titreşim (haptik / rumble) şiddetini kademeli azaltır veya artırır (7 kademe: %0 ↔ %17 ↔ %33 ↔ %50 ↔ %67 ↔ %83 ↔ %100)."),
        new("Chroma RGB (Her Yerde)", "Fn + ☰ (Menü) + ⧉ (View)",
            "Chroma RGB aydınlatma efektleri arasında geçiş yapar (Sabit ↔ Spektrum ↔ Kapalı)."),
        new("Chroma RGB (Her Yerde)", "Fn + ☰ (Menü) + D-Pad ↑ / ↓",
            "Chroma RGB parlaklığını kademeli artırır veya azaltır (5 kademe: %0 ↔ %25 ↔ %50 ↔ %75 ↔ %100)."),
        new("Chroma RGB (Her Yerde)", "Fn + ☰ (Menü) + D-Pad ← / →",
            "RGB Sabit (Static) modundayken hazır renk paleti arasında önceki / sonraki renge geçer."),
        new("Chroma RGB (Her Yerde)", "Fn + ☰ (Menü) + LB / RB",
            "RGB Spektrum modundayken animasyon hızını / alt modunu değiştirir."),
        new("Profiller (Her Yerde)", "Fn + Ⓐ / Ⓑ / Ⓧ / Ⓨ",
            "Kolun dahili hafızasındaki Profiller (Profil 1 – Profil 4) arasında anında geçiş yapar."),
        new("3.5 mm Kulaklık (Her Yerde)", "Fn + D-Pad ↑ / ↓",
            "Kola takılı 3.5 mm kulaklığın ses seviyesini artırır / azaltır (☰ Menü basılı değilken)."),
        new("3.5 mm Kulaklık (Her Yerde)", "Fn + D-Pad ← / →",
            "3.5 mm kulaklık oyun / sohbet ses dengesini ayarlar (☰ Menü basılı değilken)."),
        new("3.5 mm Kulaklık (Her Yerde)", "Fn + ⧉ (View)",
            "3.5 mm kulaklık mikrofonunu sessize alır / açar (☰ Menü basılı değilken)."),
        new("Bağlantı Modu (Her Yerde)", "○ (Razer) + ☰ (Menü) + Ⓐ (2 sn basılı)",
            "Kolu Xbox modundan PC moduna geçirir (bağlantı kesilip PC modunda yeniden bağlanır)."),
        new("Bağlantı Modu (Her Yerde)", "○ (Razer) + ☰ (Menü) + Ⓑ (2 sn basılı)",
            "Kolu Bluetooth / Kablosuz eşleştirme moduna alır veya bağlantıyı yeniler.")
    ];

    public static IReadOnlyList<ShortcutAction> AllActions { get; } = Enum.GetValues<ShortcutAction>();

    public static string ActionLabel(ShortcutAction action) => action switch
    {
        ShortcutAction.VibrationLevelUp => "Titreşim gücünü artır (+%25)",
        ShortcutAction.VibrationLevelDown => "Titreşim gücünü azalt (-%25)",
        ShortcutAction.CycleOutputLevel => "Titreşim gücünü döndür (%25 → %50 → %75 → %100)",
        ShortcutAction.RgbBrightnessUp => "RGB parlaklığını artır (+%25)",
        ShortcutAction.RgbBrightnessDown => "RGB parlaklığını azalt (-%25)",
        ShortcutAction.ToggleRgbPower => "RGB Işıkları Aç / Kapat",
        ShortcutAction.CycleRgbEffect => "Sonraki RGB Efektine Geç",
        ShortcutAction.TogglePalmAudio => "Sesten avuç titreşimini Aç / Kapat",
        ShortcutAction.ToggleMelody => "Melodi modunu Aç / Kapat",
        ShortcutAction.CycleOutput => "Sonraki sanal kol moduna geç",
        ShortcutAction.OutputPhysical => "Kol modu: Kolun kendisi (Wolverine)",
        ShortcutAction.OutputXbox360 => "Kol modu: Xbox 360 (XInput)",
        ShortcutAction.OutputXboxSeries => "Kol modu: Xbox Series (Tetikli)",
        ShortcutAction.OutputDualShock4 => "Kol modu: PS4 (DualShock 4)",
        ShortcutAction.OutputDualSense => "Kol modu: DualSense (PS5)",
        ShortcutAction.SwitchToXboxMode => "Kolu PC modundan Xbox moduna geçir",
        ShortcutAction.ToggleWindowVisibility => "RW3 penceresini Göster / Gizle",
        _ => action.ToString()
    };

    public static List<UserShortcut> CreateDefaultShortcuts() =>
    [
        new UserShortcut
        {
            Id = "def-vib-up",
            Name = "Titreşim Gücünü Artır",
            Kind = ShortcutTriggerKind.ControllerCombo,
            ControllerButtons = (ushort)(ButtonView | ButtonRightShoulder),
            HoldMs = 0,
            Action = ShortcutAction.VibrationLevelUp,
            Enabled = true
        },
        new UserShortcut
        {
            Id = "def-vib-down",
            Name = "Titreşim Gücünü Azalt",
            Kind = ShortcutTriggerKind.ControllerCombo,
            ControllerButtons = (ushort)(ButtonView | ButtonLeftShoulder),
            HoldMs = 0,
            Action = ShortcutAction.VibrationLevelDown,
            Enabled = true
        },
        new UserShortcut
        {
            Id = "def-toggle-audio",
            Name = "Sesten Avuç Titreşimini Aç/Kapat",
            Kind = ShortcutTriggerKind.ControllerCombo,
            ControllerButtons = (ushort)(ButtonView | ButtonDpadUp),
            HoldMs = 0,
            Action = ShortcutAction.TogglePalmAudio,
            Enabled = true
        },
        new UserShortcut
        {
            Id = "def-toggle-rgb",
            Name = "RGB Işıkları Aç/Kapat",
            Kind = ShortcutTriggerKind.ControllerCombo,
            ControllerButtons = (ushort)(ButtonView | ButtonDpadDown),
            HoldMs = 0,
            Action = ShortcutAction.ToggleRgbPower,
            Enabled = true
        },
        new UserShortcut
        {
            Id = "def-cycle-mode",
            Name = "Oyun İçinde Sanal Kol Modu Değiştir",
            Kind = ShortcutTriggerKind.ControllerCombo,
            ControllerButtons = (ushort)(ButtonView | ButtonMenu),
            HoldMs = 400,
            Action = ShortcutAction.CycleOutput,
            Enabled = true
        }
    ];

    public static int CountBits(ushort mask)
    {
        int count = 0;
        while (mask != 0)
        {
            count += mask & 1;
            mask >>= 1;
        }
        return count;
    }

    public static string FormatControllerCombo(ushort mask, int holdMs = 0)
    {
        if (mask == 0) return "Tuş seçilmedi";
        var parts = new List<string>();
        foreach (var (bit, label) in ButtonNames)
        {
            if ((mask & bit) != 0) parts.Add(label);
        }
        if (parts.Count == 0) parts.Add($"0x{mask:X4}");
        string combo = string.Join(" + ", parts);
        holdMs = ControllerShortcutTracker.EffectiveHoldMs(holdMs);
        if (holdMs >= 200)
            combo += $" ({holdMs / 1000.0:0.#} sn basılı)";
        return combo;
    }

    public static string FormatKeyboardHotkey(int modifiers, int vk)
    {
        if (vk <= 0) return "Tuş seçilmedi";
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(VirtualKeyName(vk));
        return string.Join(" + ", parts);
    }

    public static string VirtualKeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x60 and <= 0x69 => "NumPad " + (vk - 0x60),
        0x20 => "Space",
        0x09 => "Tab",
        0x1B => "Esc",
        0x25 => "Sol Ok",
        0x26 => "Yukarı Ok",
        0x27 => "Sağ Ok",
        0x28 => "Aşağı Ok",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        _ => $"VK_0x{vk:X2}"
    };

    /// <summary>
    /// Validates a shortcut before adding/saving:
    /// - Controller combos must have at least 2 buttons (or 1 button with ≥ 500 ms hold) so normal game presses do not collide.
    /// - Keyboard hotkeys must have a non-zero virtual key and at least one modifier (unless F1..F24).
    /// </summary>
    public static (bool Ok, string? Error) Validate(UserShortcut shortcut)
    {
        if (shortcut == null) return (false, "Kısayol boş.");
        if (shortcut.Kind == ShortcutTriggerKind.ControllerCombo)
        {
            int bits = CountBits(shortcut.ControllerButtons);
            if (bits == 0)
                return (false, "Kumandadan en az iki tuş kombinasyonu yakala (örn. ⧉ View + D-Pad ↑).");
            if (bits == 1 && shortcut.HoldMs < 500)
                return (false, "Tek tuş oyunlarla çakışır! Lütfen en az 2 tuşa aynı anda bas ya da basılı tutma süresini 0.5 sn veya üstü seç.");
            return (true, null);
        }
        else
        {
            if (shortcut.VirtualKey <= 0)
                return (false, "Klavyeden bir tuş kombinasyonu yakala (örn. Ctrl + Alt + M).");
            bool isFunctionKey = shortcut.VirtualKey is >= 0x70 and <= 0x87;
            if (shortcut.KeyModifiers == 0 && !isFunctionKey)
                return (false, "Oyunlarla çakışmaması için Ctrl, Alt veya Shift ile birlikte bir tuşa bas (veya F1–F12 kullan).");
            return (true, null);
        }
    }
}

/// <summary>
/// Stateful tracker for controller button combination shortcuts. Evaluates 30 ms ticks and fires each matching combo
/// once per hold cycle when all combo bits are held for at least <see cref="UserShortcut.HoldMs"/>.
/// </summary>
public sealed class ControllerShortcutTracker
{
    /// <summary>Every controller combo must be held this long before it fires. Two-button combos at 0 ms fired during
    /// play: in Banishers, ⧉ View + D-Pad ↑ switched the palm vibration off on 2026-11-24 23:58 and 2026-11-25 21:48,
    /// and the user saw it as "vibration died and never came back" (E-20261125-90). Games press such pairs together;
    /// nobody holds them for a second by accident.</summary>
    public const int MinHoldMs = 1000;

    public static int EffectiveHoldMs(int holdMs) => Math.Max(holdMs, MinHoldMs);

    private readonly Dictionary<string, long> _comboStartTick = new();
    private readonly HashSet<string> _firedThisHold = new();

    public IReadOnlyList<UserShortcut> Evaluate(IReadOnlyList<UserShortcut>? shortcuts, ushort currentButtons, long nowTickMs)
    {
        if (shortcuts == null || shortcuts.Count == 0 || currentButtons == 0)
        {
            if (currentButtons == 0)
            {
                _comboStartTick.Clear();
                _firedThisHold.Clear();
            }
            return [];
        }

        // Order by most specific (more buttons) first so a 3-button combo wins over a 2-button subset.
        List<UserShortcut>? triggered = null;
        foreach (var sc in shortcuts
                     .Where(s => s.Enabled && s.Kind == ShortcutTriggerKind.ControllerCombo && s.ControllerButtons != 0)
                     .OrderByDescending(s => ShortcutManager.CountBits(s.ControllerButtons)))
        {
            bool held = (currentButtons & sc.ControllerButtons) == sc.ControllerButtons;
            if (!held)
            {
                _comboStartTick.Remove(sc.Id);
                _firedThisHold.Remove(sc.Id);
                continue;
            }

            if (_firedThisHold.Contains(sc.Id))
                continue;

            if (!_comboStartTick.TryGetValue(sc.Id, out long startTick))
            {
                startTick = nowTickMs;
                _comboStartTick[sc.Id] = startTick;
            }

            if (nowTickMs - startTick >= EffectiveHoldMs(sc.HoldMs))
            {
                _firedThisHold.Add(sc.Id);
                triggered ??= new List<UserShortcut>();
                triggered.Add(sc);
            }
        }

        return triggered ?? (IReadOnlyList<UserShortcut>)[];
    }
}
