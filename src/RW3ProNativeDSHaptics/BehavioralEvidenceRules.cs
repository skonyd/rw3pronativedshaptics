using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Reports subjective tactile response, never a hardware sensor measurement.
/// No rating can be assigned to a controller that was not included in playback.
/// </summary>
public enum PhysicalSensation { NotEvaluated, Felt, NotFelt, Uncertain }

public sealed record TactileRatings(int? Texture, int? Impact, int? Stereo)
{
    public static TactileRatings Empty { get; } = new(null, null, null);
    public bool IsEmpty => Texture is null && Impact is null && Stereo is null;
    public bool IsComplete => Texture is >= 1 and <= 5 &&
                              Impact is >= 1 and <= 5 &&
                              Stereo is >= 1 and <= 5;
    public double Mean => IsComplete ? (Texture!.Value + Impact!.Value + Stereo!.Value) / 3.0 : 0;
}

public static class BehavioralEvidenceRules
{
    public static bool IncludesDualSense(ComparisonTarget target) =>
        target is ComparisonTarget.DualSenseOnly or ComparisonTarget.Both;
    public static bool IncludesWolverine(ComparisonTarget target) =>
        target is ComparisonTarget.WolverineOnly or ComparisonTarget.Both;

    public static bool CanSave(
        ComparisonTarget target,
        PhysicalSensation ds, PhysicalSensation rw,
        TactileRatings dsRatings, TactileRatings rwRatings)
    {
        if (!Enum.IsDefined(target) || dsRatings == null || rwRatings == null) return false;
        return CanSaveSide(IncludesDualSense(target), ds, dsRatings) &&
               CanSaveSide(IncludesWolverine(target), rw, rwRatings);
    }

    private static bool CanSaveSide(bool played, PhysicalSensation felt, TactileRatings ratings)
    {
        if (!Enum.IsDefined(felt)) return false;
        if (!played) return felt == PhysicalSensation.NotEvaluated && ratings.IsEmpty;
        // Complete ratings only when the participant confirms physical vibration.
        if (felt == PhysicalSensation.Felt) return ratings.IsComplete;
        // NotFelt/Uncertain are useful negative results; no invented 1-5 scores.
        return felt is PhysicalSensation.NotFelt or PhysicalSensation.Uncertain &&
               ratings.IsEmpty;
    }
}

public sealed record BehavioralEvidenceEntry(
    int SchemaVersion,
    DateTimeOffset Utc,
    HapticBehavior Scene,
    ComparisonTarget Target,
    bool UsbPcmSubmitted,
    bool RazerProviderTicked,
    bool UsbWasapiExclusive,
    string? UsbEndpointName,
    int RwGlobalLevelPercent,
    int RwSceneLevelPercent,
    PhysicalSensation DsSensation,
    PhysicalSensation RwSensation,
    TactileRatings DsRatings,
    TactileRatings RwRatings,
    string Notes)
{
    public const int Schema = 2;

    public bool Valid => SchemaVersion == Schema && Enum.IsDefined(Scene) &&
        RwGlobalLevelPercent is >= 0 and <= 100 &&
        RwSceneLevelPercent is >= 0 and <= 100 &&
        BehavioralEvidenceRules.CanSave(
            Target, DsSensation, RwSensation, DsRatings, RwRatings);
}

public sealed record BehavioralEvidenceSummary(
    int SceneCount, int PairedSceneCount, double DsMean, double RwMean)
{
    public string Display =>
        "Bu yerel raporda " + SceneCount + "/10 farklı sahne değerlendirildi. " +
        "İki kola da hissedildi onayıyla puanlanan: " + PairedSceneCount + "/10. " +
        (PairedSceneCount > 0
            ? "Eşlenik öznel puan ortalaması: DualSense " + DsMean.ToString("F2") +
              "/5; Wolverine " + RwMean.ToString("F2") + "/5. "
            : "Henüz eşlenik puan ortalaması yok. ") +
        "Bu veriler fiziksel ölçüm veya özgün oyun haptic sadakati kanıtı değildir.";
}

public static class BehavioralEvidenceReport
{
    public static BehavioralEvidenceSummary Summarize(IEnumerable<BehavioralEvidenceEntry> data)
    {
        // Prefer the latest *paired valid report* for each scene; do not
        // accidentally count incomplete/single-device sessions as paired.
        var valid = data.Where(x => x.Valid).ToArray();
        var pairs = valid.Where(x =>
            x.Target == ComparisonTarget.Both &&
            x.DsSensation == PhysicalSensation.Felt &&
            x.RwSensation == PhysicalSensation.Felt &&
            x.DsRatings.IsComplete && x.RwRatings.IsComplete)
            .GroupBy(x => x.Scene)
            .Select(x => x.OrderByDescending(y => y.Utc).First())
            .ToArray();
        return new BehavioralEvidenceSummary(
            valid.Select(x => x.Scene).Distinct().Count(), pairs.Length,
            pairs.Length == 0 ? 0 : pairs.Average(x => x.DsRatings.Mean),
            pairs.Length == 0 ? 0 : pairs.Average(x => x.RwRatings.Mean));
    }
}
