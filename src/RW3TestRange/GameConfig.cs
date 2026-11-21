using System.Text.Json;

namespace RW3TestRange;

/// <summary>The pad type the test game "supports" in the quick table (same columns as RW3's output selector).</summary>
public enum PadProfile { Physical, Xbox360, XboxSeries, DualShock4, DualSense }

/// <summary>Quick-table rows (same idea as RW3's feature matrix, from the game's side).</summary>
[Flags]
public enum GameFeature { None = 0, Music = 1, Sfx = 2, HdHaptics = 4, Rumble = 8, Triggers = 16 }

/// <summary>Trigger effect intensity, as PS5 games and the PS5 system setting offer it.</summary>
public enum TriggerIntensity { Off, Weak, Medium, Strong }

/// <summary>Game settings, saved in %LOCALAPPDATA%\RW3TestRange\config.json.</summary>
public sealed class GameConfig
{
    public PadProfile Profile { get; set; } = PadProfile.DualSense;
    public GameFeature Features { get; set; } = GameFeature.Music | GameFeature.Sfx | GameFeature.HdHaptics | GameFeature.Rumble | GameFeature.Triggers;
    /// <summary>Manual output override (advanced checkboxes); null = derived from Profile + Features.</summary>
    public Out? ManualOutputs { get; set; }
    public bool[] Loadout { get; set; } = { true, true, true, true, true };
    public TriggerIntensity Triggers { get; set; } = TriggerIntensity.Strong;
    public int HapticPercent { get; set; } = 100;
    public int RumblePercent { get; set; } = 100;
    public int MusicPercent { get; set; } = 60;
    public int SfxPercent { get; set; } = 90;
    public int RoundSeconds { get; set; } = 60;
    public bool InvertY { get; set; }
    public bool GyroAim { get; set; }
    public string? EndpointName { get; set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3TestRange", "config.json");

    public static GameConfig Load()
    {
        try
        {
            var c = File.Exists(FilePath) ? JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(FilePath)) : null;
            c ??= new GameConfig();
            if (c.Loadout.Length != RangeGame.Weapons.Length) c.Loadout = Enumerable.Repeat(true, RangeGame.Weapons.Length).ToArray();
            return c;
        }
        catch { return new GameConfig(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    /// <summary>What a real game supporting <paramref name="p"/> can do with that pad.</summary>
    public static bool Supports(PadProfile p, GameFeature f) => f switch
    {
        GameFeature.Music or GameFeature.Sfx => true,
        GameFeature.HdHaptics => p == PadProfile.DualSense,
        GameFeature.Rumble => true,
        GameFeature.Triggers => p is PadProfile.Physical or PadProfile.XboxSeries or PadProfile.DualSense,
        _ => false
    };

    /// <summary>The output paths such a game uses: Xbox One/Series pads through GameInput (rumble + trigger motors), the
    /// Xbox 360 pad through XInput, PS4 / PS5 pads through their HID reports and (PS5) the haptic audio channels.</summary>
    public static Out OutputsFor(PadProfile p, GameFeature f)
    {
        Out o = Out.None;
        if (f.HasFlag(GameFeature.Music)) o |= Out.Music;
        if (f.HasFlag(GameFeature.Sfx)) o |= Out.Sfx;
        bool feedback = f.HasFlag(GameFeature.Rumble) || f.HasFlag(GameFeature.Triggers) && Supports(p, GameFeature.Triggers);
        switch (p)
        {
            case PadProfile.Xbox360: if (f.HasFlag(GameFeature.Rumble)) o |= Out.XInput; break;
            case PadProfile.Physical or PadProfile.XboxSeries: if (feedback) o |= Out.GameInput; break;
            case PadProfile.DualShock4: if (f.HasFlag(GameFeature.Rumble)) o |= Out.Ds4; break;
            case PadProfile.DualSense:
                o |= Out.DsHid;   // light / LEDs always; triggers and rumble per row
                if (f.HasFlag(GameFeature.HdHaptics)) o |= Out.DsHaptics;
                break;
        }
        return o;
    }

    public static string Label(PadProfile p) => p switch
    {
        PadProfile.Physical => "Xbox (kolun kendisi)", PadProfile.Xbox360 => "Xbox 360", PadProfile.XboxSeries => "Xbox Series",
        PadProfile.DualShock4 => "PS4", _ => "DualSense"
    };

    public static string Label(GameFeature f) => f switch
    {
        GameFeature.Music => "Müzik", GameFeature.Sfx => "Efekt sesleri", GameFeature.HdHaptics => "HD haptik",
        GameFeature.Rumble => "Rumble", GameFeature.Triggers => "Tetik titreşimi / efekti", _ => f.ToString()
    };

    /// <summary>Trigger effect strength 1..8 scaled by the intensity setting (0 = effect off).</summary>
    public int Strength(int full) => Triggers switch
    {
        TriggerIntensity.Off => 0, TriggerIntensity.Weak => Math.Max(1, (int)Math.Round(full * .4)),
        TriggerIntensity.Medium => Math.Max(1, (int)Math.Round(full * .7)), _ => full
    };

    /// <summary>RW3's current choice from its settings.json (Output / Features), mapped onto the quick table.</summary>
    public static (PadProfile Profile, GameFeature Features, string Text)? FromRw3()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "settings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("Output", out var o) || o.ValueKind != JsonValueKind.Number) return null;
            int output = o.GetInt32(), features = root.TryGetProperty("Features", out var fe) ? fe.GetInt32() : 0;
            var profile = (PadProfile)Math.Clamp(output, 0, 4);
            // RW3 rows: 1 sound → palms, 2 game HD haptics, 4 game rumble, 8 game triggers
            var f = GameFeature.Music | GameFeature.Sfx;
            if ((features & 2) != 0) f |= GameFeature.HdHaptics;
            if ((features & 4) != 0 || profile == PadProfile.Physical) f |= GameFeature.Rumble;
            if ((features & 8) != 0 || profile == PadProfile.Physical) f |= GameFeature.Triggers;
            return (profile, f, $"RW3: {Label(profile)} · satırlar 0x{features:X}");
        }
        catch { return null; }
    }
}
