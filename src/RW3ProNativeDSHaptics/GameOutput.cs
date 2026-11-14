using System;

namespace RW3ProNativeDSHaptics;

/// <summary>Which controller the game sees (the DSX-style "Output" choice on the main page).</summary>
public enum GameOutput
{
    /// <summary>The Wolverine itself: no virtual pad; the game drives its rumble and triggers directly.</summary>
    Physical,
    /// <summary>A virtual Xbox 360 pad (XUSB): two rumble motors, no impulse triggers. Game Pass games accept it.</summary>
    Xbox360,
    /// <summary>A virtual Xbox Series pad: rumble and impulse triggers. Some GDK games ignore its input (E-20261115-02).</summary>
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
    GameTriggers = 8
}

public enum FeatureSupport
{
    No,
    Yes,
    /// <summary>The game drives the physical pad itself: always on, RW3 neither adds nor removes it.</summary>
    ByGame
}

/// <summary>What the existing pipelines run for an output + feature set.</summary>
public sealed record OutputPlan(HapticMode Mode, string? VirtualProfile, bool Palms, NativeHapticSource Source, bool Rumble, bool Triggers)
{
    public bool NeedsAdmin => VirtualProfile != null || Mode == HapticMode.Native;
}

/// <summary>Pure rules of the output selector and the feature matrix (no UI, contract-tested).</summary>
public static class OutputMatrix
{
    public static readonly GameOutput[] Outputs =
        { GameOutput.Physical, GameOutput.Xbox360, GameOutput.XboxSeries, GameOutput.DualShock4, GameOutput.DualSense };

    public static readonly OutputFeature[] Features =
        { OutputFeature.PalmAudio, OutputFeature.PalmGame, OutputFeature.GameRumble, OutputFeature.GameTriggers };

    public static FeatureSupport Support(GameOutput output, OutputFeature feature) => feature switch
    {
        OutputFeature.PalmAudio => FeatureSupport.Yes,
        OutputFeature.PalmGame => output == GameOutput.DualSense ? FeatureSupport.Yes : FeatureSupport.No,
        OutputFeature.GameRumble => output == GameOutput.Physical ? FeatureSupport.ByGame : FeatureSupport.Yes,
        OutputFeature.GameTriggers => output switch
        {
            GameOutput.Physical => FeatureSupport.ByGame,
            GameOutput.XboxSeries or GameOutput.DualSense => FeatureSupport.Yes,
            _ => FeatureSupport.No
        },
        _ => FeatureSupport.No
    };

    /// <summary>Keeps only the features RW3 can switch on this output, with one palm source at most.</summary>
    public static OutputFeature Normalize(GameOutput output, OutputFeature set)
    {
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
        _ => Normalize(output, OutputFeature.PalmAudio | OutputFeature.GameRumble | OutputFeature.GameTriggers)
    };

    /// <summary>The closest output that supports <paramref name="feature"/>: DualSense for its HD haptics; for triggers
    /// the Series pad from the Xbox side, the DualSense from the PlayStation side.</summary>
    public static GameOutput BestFor(OutputFeature feature, GameOutput current) =>
        Support(current, feature) == FeatureSupport.Yes ? current : feature switch
        {
            OutputFeature.PalmGame => GameOutput.DualSense,
            OutputFeature.GameTriggers => current == GameOutput.DualShock4 ? GameOutput.DualSense : GameOutput.XboxSeries,
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
            _ => new(HapticMode.Audio, Profile(output), palms, NativeHapticSource.Off, rumble, triggers)
        };
    }

    /// <summary>HIDMaestro profile of a virtual pad output.</summary>
    public static string? Profile(GameOutput output) => output switch
    {
        GameOutput.Xbox360 => "xbox-360-wired",
        GameOutput.XboxSeries => "xbox-series-xs-bt",
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
        _ => feature.ToString()
    };

    public static string Describe(GameOutput output) => output switch
    {
        GameOutput.Physical => "Oyun Wolverine'i olduğu gibi görür; rumble ve tetik titreşimini oyun kendisi gönderir. Game Pass oyunları ve tarayıcılar öndeyken Xbox modunda avuçlar susabilir.",
        GameOutput.Xbox360 => "Oyun bir Xbox 360 kolu görür. Neredeyse her oyun destekler (Game Pass dahil); tetik titreşimi yoktur. Yönetici izni ister.",
        GameOutput.XboxSeries => "Oyun bir Xbox Series kolu görür: rumble + tetik titreşimi. Bazı Game Pass oyunları bu kolun girdisini almıyor; o zaman Xbox 360'ı seç.",
        GameOutput.DualShock4 => "Oyun bir PS4 kolu görür: PlayStation tuş simgeleri ve rumble. Tetik titreşimi yoktur.",
        _ => "Oyun bir DualSense görür: HD haptik avuçlarda, tetik efektleri tetik motorlarında. DualSense destekli oyunlar için."
    };
}
