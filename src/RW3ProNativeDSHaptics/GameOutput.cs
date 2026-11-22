using System;

namespace RW3ProNativeDSHaptics;

/// <summary>Which controller the game sees (the DSX-style "Output" choice on the main page).</summary>
public enum GameOutput
{
    /// <summary>The Wolverine itself: no virtual pad; the game drives its rumble and triggers directly.</summary>
    Physical,
    /// <summary>A virtual Xbox 360 pad (XUSB): two rumble motors, no impulse triggers. Game Pass games accept it.</summary>
    Xbox360,
    /// <summary>A virtual Xbox Series pad: rumble; Windows handed it the test range's trigger rumble as 0
    /// (E-20261122-12), so trigger vibration normally comes from RW3. The game's triggers stay selectable as an
    /// experiment (user request). Works in Banishers on a freshly booted system (E-20261122-14).</summary>
    XboxSeries,
    /// <summary>A virtual DualShock 4: rumble only.</summary>
    DualShock4,
    /// <summary>The virtual DualSense: HD haptics, adaptive triggers, rumble.</summary>
    DualSense
}

/// <summary>Feedback rows of the feature matrix. Several can be on at once where the output supports them.</summary>
[Flags]
public enum OutputFeature
{
    None = 0,
    /// <summary>The PC's sound → palms (Sensa).</summary>
    PalmAudio = 1,
    /// <summary>The game's DualSense HD haptics → palms. Excludes <see cref="PalmAudio"/> (one producer for the palms).</summary>
    PalmGame = 2,
    /// <summary>The game's rumble → body motors.</summary>
    GameRumble = 4,
    /// <summary>The game's trigger vibration (Xbox impulse triggers, DualSense trigger effects) → trigger motors.</summary>
    GameTriggers = 8,
    /// <summary>RW3 makes trigger-motor vibration itself, from the game's rumble hits and the sound's hits, for pads that
    /// carry no trigger channel from the game (Xbox 360, PS4, and the Series pad: Windows sends it trigger rumble as 0,
    /// E-20261122-12). The trigger motors exist only in Xbox mode.</summary>
    SynthTriggers = 16
}

public enum FeatureSupport
{
    No,
    Yes,
    /// <summary>The game drives the physical pad itself: always on, RW3 neither adds nor removes it.</summary>
    ByGame
}

/// <summary>What the existing pipelines run for an output + feature set.</summary>
public sealed record OutputPlan(HapticMode Mode, string? VirtualProfile, bool Palms, NativeHapticSource Source, bool Rumble, bool Triggers,
    bool SynthTriggers = false)
{
    public bool NeedsAdmin => VirtualProfile != null || Mode == HapticMode.Native;
}

/// <summary>One HIDMaestro pad the "Xbox Series" output can be.</summary>
public sealed record SeriesPadType(string Profile, string Label, string Ids)
{
    public string Display => Label + "  (" + Ids + ")";
}

/// <summary>Pure rules of the output selector and the feature matrix (no UI, contract-tested).</summary>
public static class OutputMatrix
{
    public static readonly GameOutput[] Outputs =
        { GameOutput.Physical, GameOutput.Xbox360, GameOutput.XboxSeries, GameOutput.DualShock4, GameOutput.DualSense };

    public static readonly OutputFeature[] Features =
        { OutputFeature.PalmAudio, OutputFeature.PalmGame, OutputFeature.GameRumble, OutputFeature.GameTriggers, OutputFeature.SynthTriggers };

    public static FeatureSupport Support(GameOutput output, OutputFeature feature) => feature switch
    {
        OutputFeature.PalmAudio => FeatureSupport.Yes,
        OutputFeature.PalmGame => output == GameOutput.DualSense ? FeatureSupport.Yes : FeatureSupport.No,
        OutputFeature.GameRumble => output == GameOutput.Physical ? FeatureSupport.ByGame : FeatureSupport.Yes,
        // Series: experimental. Windows (xinputhid) handed the test range's trigger rumble to it as 0 in every report
        // (E-20261122-12); kept selectable so other games can be tried. RW3's triggers can run alongside.
        OutputFeature.GameTriggers => output switch
        {
            GameOutput.Physical => FeatureSupport.ByGame,
            GameOutput.XboxSeries or GameOutput.DualSense => FeatureSupport.Yes,
            _ => FeatureSupport.No
        },
        OutputFeature.SynthTriggers => output is GameOutput.Xbox360 or GameOutput.XboxSeries or GameOutput.DualShock4 ? FeatureSupport.Yes : FeatureSupport.No,
        _ => FeatureSupport.No
    };

    /// <summary>Keeps only the features RW3 can switch on this output, with one palm source at most. A request for the
    /// game's triggers becomes RW3's triggers where the game cannot drive them (settings saved before 2026-11-22 had
    /// game triggers on the Series pad).</summary>
    public static OutputFeature Normalize(GameOutput output, OutputFeature set)
    {
        if (set.HasFlag(OutputFeature.GameTriggers) && Support(output, OutputFeature.GameTriggers) == FeatureSupport.No &&
            Support(output, OutputFeature.SynthTriggers) == FeatureSupport.Yes) set |= OutputFeature.SynthTriggers;
        var result = OutputFeature.None;
        foreach (var f in Features)
            if (set.HasFlag(f) && Support(output, f) == FeatureSupport.Yes) result |= f;
        if (result.HasFlag(OutputFeature.PalmAudio) && result.HasFlag(OutputFeature.PalmGame)) result &= ~OutputFeature.PalmAudio;
        return result;
    }

    /// <summary>The features turned on when the user picks an output: what still fits from before, or that output's
    /// useful default when nothing fits.</summary>
    public static OutputFeature OnSelect(GameOutput output, OutputFeature current)
    {
        var kept = Normalize(output, current);
        if (output == GameOutput.DualSense && current.HasFlag(OutputFeature.PalmAudio) && !current.HasFlag(OutputFeature.PalmGame))
            kept = (kept & ~OutputFeature.PalmGame) | OutputFeature.PalmAudio;
        return kept != OutputFeature.None ? kept : Defaults(output);
    }

    public static OutputFeature Defaults(GameOutput output) => output switch
    {
        GameOutput.Physical => OutputFeature.PalmAudio,
        GameOutput.DualSense => OutputFeature.PalmGame | OutputFeature.GameRumble | OutputFeature.GameTriggers,
        _ => Normalize(output, OutputFeature.PalmAudio | OutputFeature.GameRumble | OutputFeature.GameTriggers | OutputFeature.SynthTriggers)
    };

    /// <summary>The closest output that supports <paramref name="feature"/>: DualSense for its HD haptics; for the game's
    /// triggers the Wolverine itself from the Xbox side, the DualSense from the PlayStation side.</summary>
    public static GameOutput BestFor(OutputFeature feature, GameOutput current) =>
        Support(current, feature) == FeatureSupport.Yes ? current : feature switch
        {
            OutputFeature.PalmGame => GameOutput.DualSense,
            OutputFeature.GameTriggers => current == GameOutput.DualShock4 ? GameOutput.DualSense : GameOutput.Physical,
            OutputFeature.SynthTriggers => current == GameOutput.DualSense ? GameOutput.DualShock4 : GameOutput.Xbox360,
            _ => current == GameOutput.Physical ? GameOutput.Xbox360 : current
        };

    /// <summary>A click on a matrix cell. <paramref name="column"/> is the output whose cell was clicked (null = the
    /// current one). A cell of another output, or a feature the current one lacks, switches the output and turns the
    /// feature on; a cell of the current output toggles it. "By the game" cells change nothing.</summary>
    public static (GameOutput Output, OutputFeature Features) Click(GameOutput current, OutputFeature set, OutputFeature feature, GameOutput? column = null)
    {
        var target = column ?? current;
        var support = Support(target, feature);
        if (support == FeatureSupport.ByGame) return (target, target == current ? set : OnSelect(target, set));
        if (support == FeatureSupport.No) target = BestFor(feature, target);
        if (Support(target, feature) == FeatureSupport.ByGame) return (target, OnSelect(target, set));
        if (Support(target, feature) != FeatureSupport.Yes) return (current, set);
        bool on = target != current || !set.HasFlag(feature);
        var next = target == current ? set : OnSelect(target, set);
        if (on)
        {
            next |= feature;
            if (feature == OutputFeature.PalmAudio) next &= ~OutputFeature.PalmGame;
            if (feature == OutputFeature.PalmGame) next &= ~OutputFeature.PalmAudio;
        }
        else next &= ~feature;
        return (target, Normalize(target, next));
    }

    public static OutputPlan Plan(GameOutput output, OutputFeature set)
    {
        set = Normalize(output, set);
        bool palms = set.HasFlag(OutputFeature.PalmAudio), rumble = set.HasFlag(OutputFeature.GameRumble), triggers = set.HasFlag(OutputFeature.GameTriggers);
        return output switch
        {
            GameOutput.Physical => new(palms ? HapticMode.Audio : HapticMode.Normal, null, palms, NativeHapticSource.Off, false, false),
            GameOutput.DualSense => new(HapticMode.Native, null, false,
                set.HasFlag(OutputFeature.PalmGame) ? NativeHapticSource.Game : palms ? NativeHapticSource.Audio : NativeHapticSource.Off, rumble, triggers),
            _ => new(HapticMode.Audio, Profile(output), palms, NativeHapticSource.Off, rumble, triggers, set.HasFlag(OutputFeature.SynthTriggers))
        };
    }

    /// <summary>The HIDMaestro Xbox One/Series pads the user can try for <see cref="GameOutput.XboxSeries"/>: all carry
    /// impulse triggers, but a game may accept only some of them (Banishers opened the Series BT pad and ignored its
    /// input, E-20261122-03).</summary>
    public static readonly SeriesPadType[] SeriesPadTypes =
    {
        new("xbox-series-xs-bt", "Xbox Series X|S · Bluetooth", "045E:0B13"),
        new("xbox-series-xs", "Xbox Series X|S · USB", "045E:0B12"),
        new("xbox-one-s", "Xbox One S · USB", "045E:02EA"),
        new("xbox-one-s-bt", "Xbox One S · Bluetooth", "045E:02FD"),
        new("xbox-elite-v2", "Xbox Elite Series 2 · USB", "045E:0B00"),
        new("xbox-elite-v2-bt", "Xbox Elite Series 2 · Bluetooth", "045E:0B05")
    };

    public const string DefaultSeriesProfile = "xbox-series-xs-bt";

    /// <summary>The chosen Series pad type (from the settings at start; unknown ids fall back to the default).</summary>
    public static string SeriesProfile
    {
        get => s_seriesProfile;
        set => s_seriesProfile = Array.Exists(SeriesPadTypes, t => t.Profile == value) ? value! : DefaultSeriesProfile;
    }
    private static string s_seriesProfile = DefaultSeriesProfile;

    /// <summary>HIDMaestro profile of a virtual pad output.</summary>
    public static string? Profile(GameOutput output) => output switch
    {
        GameOutput.Xbox360 => "xbox-360-wired",
        GameOutput.XboxSeries => SeriesProfile,
        GameOutput.DualShock4 => "dualshock-4-v2",
        _ => null
    };

    /// <summary>Settings written before the selector existed: the old mode and its sources.</summary>
    public static (GameOutput Output, OutputFeature Features) FromLegacy(HapticMode mode, NativeHapticSource? source, bool rumble, bool triggers) => mode switch
    {
        HapticMode.Audio => (GameOutput.Physical, OutputFeature.PalmAudio),
        HapticMode.Native => (GameOutput.DualSense,
            (source switch { NativeHapticSource.Audio => OutputFeature.PalmAudio, NativeHapticSource.Off => OutputFeature.None, _ => OutputFeature.PalmGame }) |
            (rumble ? OutputFeature.GameRumble : 0) | (triggers ? OutputFeature.GameTriggers : 0)),
        _ => (GameOutput.Physical, OutputFeature.None)
    };

    public static string Label(GameOutput output) => output switch
    {
        GameOutput.Physical => "Kolun kendisi",
        GameOutput.Xbox360 => "Xbox 360",
        GameOutput.XboxSeries => "Xbox Series",
        GameOutput.DualShock4 => "PS4",
        _ => "DualSense"
    };

    public static string Label(OutputFeature feature) => feature switch
    {
        OutputFeature.PalmAudio => "Sesten avuç titreşimi",
        OutputFeature.PalmGame => "Oyunun HD haptiği",
        OutputFeature.GameRumble => "Oyunun rumble titreşimi",
        OutputFeature.GameTriggers => "Oyunun tetik titreşimi",
        OutputFeature.SynthTriggers => "RW3'ün tetik titreşimi",
        _ => feature.ToString()
    };

    public static string Describe(GameOutput output) => output switch
    {
        GameOutput.Physical => "Oyun Wolverine'i olduğu gibi görür; rumble ve tetik titreşimini oyun kendisi gönderir.",
        GameOutput.Xbox360 => "Oyun bir Xbox 360 kolu görür. Neredeyse her oyun destekler. Oyun tetiğe komut gönderemez; tetik titreşimini istersen RW3 üretir. Yönetici izni ister.",
        GameOutput.XboxSeries => "Oyun bir Xbox Series kolu görür: rumble. \"Oyunun tetik titreşimi\" deneysel: testte Windows oyunun tetik komutunu bu sanal kola 0 olarak iletti. \"RW3'ün tetik titreşimi\" tetikleri oyunun rumble'ından ve sesinden titretir; ikisi birlikte açık olabilir. Oyunun kendi tetik efektleri kesin olarak \"Kolun kendisi\" ile gelir. Yönetici izni ister.",
        GameOutput.DualShock4 => "Oyun bir PS4 kolu görür: PlayStation tuş simgeleri ve rumble. Oyun tetiğe komut gönderemez; tetik titreşimini istersen RW3 üretir. Yönetici izni ister.",
        _ => "Oyun bir DualSense görür: HD haptik avuçlarda, tetik efektleri tetik motorlarında. DualSense destekli oyunlar için."
    };

    /// <summary>The orange caveat under the selector (null = none): known limits and what is still untested.</summary>
    public static string? Warning(GameOutput output, bool xboxMode) => output switch
    {
        GameOutput.Physical => xboxMode
            ? "Xbox modunda Game Pass oyunları, tarayıcılar ve Claude/Codex öndeyken Windows avuçları (Sensa) susturur. Avuçlar için sanal bir kol seçebilirsin."
            : null,
        GameOutput.Xbox360 => "Deneysel: oyunda henüz denenmedi. Seçtikten sonra kolu çıkarıp tak ve oyunu yeniden başlat.",
        GameOutput.XboxSeries => "Deneysel: oyunda henüz doğrulanmadı. Oyun kolu görmüyorsa aşağıdan başka bir kol türü dene. Seçtikten sonra kolu çıkarıp tak ve oyunu yeniden başlat.",
        GameOutput.DualShock4 => "Deneysel: oyunda henüz denenmedi. Oyun PS4 kolunu desteklemiyorsa kol tepki vermez. Seçtikten sonra kolu çıkarıp tak ve oyunu yeniden başlat.",
        _ => null
    };
}
