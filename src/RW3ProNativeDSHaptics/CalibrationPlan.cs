using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RW3ProNativeDSHaptics;

/// <summary>Where a calibration stimulus is felt (the user's words: avuç = Sensa palm, tetik/ön = trigger motor, sap = body motor).</summary>
public enum CalActuator { PalmLeft, PalmRight, TriggerLeft, TriggerRight, BodyBig, BodySmall }

/// <summary>How the actuator is driven. Sensa: Razer frames (PC: HID, Xbox: GIP). Rumble: XInput (PC) / GameInput (Xbox).</summary>
public enum CalPath { Sensa, Rumble }

public enum CalTexture { Steady, Pulse, Ramp }

/// <summary>One actuator command. Pitch: palms in Hz, Sensa triggers as the raw 7-bit field, rumble ignores it.</summary>
public readonly record struct CalTone(CalActuator Actuator, CalPath Path, double Strength, int Pitch = 0, CalTexture Texture = CalTexture.Steady)
{
    /// <summary>Strength at time <paramref name="t"/>: pulse = 4 Hz on/off, ramp = rises over the whole stimulus.</summary>
    public double LevelAt(double t, double seconds) => Texture switch
    {
        CalTexture.Pulse => ((int)(t * 8) % 2 == 0) ? Strength : 0,
        CalTexture.Ramp => Strength * Math.Clamp(t / Math.Max(0.1, seconds), 0, 1),
        _ => Strength
    };

    public string Describe() =>
        $"{Actuator}/{Path} s{Strength:0.00}" + (Pitch > 0 ? (Actuator is CalActuator.PalmLeft or CalActuator.PalmRight ? $" {Pitch}Hz" : $" p{Pitch}") : "") +
        (Texture != CalTexture.Steady ? " " + Texture.ToString().ToLowerInvariant() : "");
}

public sealed record CalStimulus(IReadOnlyList<CalTone> Tones, double Seconds = 1.6)
{
    public static CalStimulus Nothing { get; } = new(Array.Empty<CalTone>());
    public string Describe() => Tones.Count == 0 ? "nothing" : string.Join(" + ", Tones.Select(t => t.Describe()));
}

public enum CalTrialKind { Locate, Stronger, Better }

/// <summary>
/// One trial. Locate: <see cref="Correct"/> is the right option. Stronger: the stronger stimulus (0 = first, 1 = second)
/// when physics says so, else -1. Better: always -1 (preference). Compare trials name their two variants; every pair is
/// played in both orders (<see cref="FirstIsA"/>) so an order bias cannot pass for a preference.
/// </summary>
public sealed record CalTrial(CalTrialKind Kind, string Question, CalStimulus First, CalStimulus? Second,
    IReadOnlyList<string> Options, int Correct, string Group, string? VariantA = null, string? VariantB = null, bool FirstIsA = true);

/// <summary>What the connected controller can do in its current mode.</summary>
public sealed record CalCapabilities(string Mode, bool SensaPalms, bool SensaTriggers, bool RumbleTriggers, bool Body)
{
    public bool Any => SensaPalms || SensaTriggers || RumbleTriggers || Body;
}

public sealed record CalResult(CalTrial Trial, int Answer, double ResponseMs, int Replays);

/// <summary>
/// Blind general calibration ("Genel kalibrasyon testi"). Shuffled trials with varied strengths, pitches and textures:
/// <list type="bullet">
/// <item>locate trials: one or two actuators at once, 3 answer choices that change every time, sometimes "nothing";</item>
/// <item>"which was stronger / which felt better, 1 or 2" pairs, each in both orders.</item>
/// </list>
/// Every locate stimulus is played twice, so consistency can be scored. Pure; the device side is <see cref="CalibrationRig"/>.
/// </summary>
public static class CalibrationPlan
{
    public const string Nothing = "Hiçbir şey";
    public static readonly string[] CompareOptions = { "1", "2", "Fark yok" };

    public static string Label(CalActuator a) => a switch
    {
        CalActuator.PalmLeft => "Sol avuç",
        CalActuator.PalmRight => "Sağ avuç",
        CalActuator.TriggerLeft => "Sol tetik (ön)",
        CalActuator.TriggerRight => "Sağ tetik (ön)",
        CalActuator.BodyBig => "Sol sap (gövde)",
        _ => "Sağ sap (gövde)"
    };

    /// <summary>The answer that names a stimulus: its actuators in a fixed order, or "nothing".</summary>
    public static string Label(CalStimulus s) =>
        s.Tones.Count == 0 || s.Tones.All(t => t.Strength <= 0) ? Nothing
        : string.Join(" + ", s.Tones.Select(t => t.Actuator).Distinct().OrderBy(a => a).Select(Label));

    private static bool IsLeft(CalActuator a) => a is CalActuator.PalmLeft or CalActuator.TriggerLeft or CalActuator.BodyBig;
    private static CalActuator Mirror(CalActuator a) => a switch
    {
        CalActuator.PalmLeft => CalActuator.PalmRight, CalActuator.PalmRight => CalActuator.PalmLeft,
        CalActuator.TriggerLeft => CalActuator.TriggerRight, CalActuator.TriggerRight => CalActuator.TriggerLeft,
        CalActuator.BodyBig => CalActuator.BodySmall, _ => CalActuator.BodyBig
    };

    public static IReadOnlyList<CalTrial> Build(CalCapabilities caps, int seed)
    {
        var rng = new Random(seed);
        var available = new List<CalActuator>();
        if (caps.SensaPalms) available.AddRange(new[] { CalActuator.PalmLeft, CalActuator.PalmRight });
        if (caps.SensaTriggers || caps.RumbleTriggers) available.AddRange(new[] { CalActuator.TriggerLeft, CalActuator.TriggerRight });
        if (caps.Body) available.AddRange(new[] { CalActuator.BodyBig, CalActuator.BodySmall });
        var trigPath = caps.SensaTriggers ? CalPath.Sensa : CalPath.Rumble;

        // ── Locate stimuli (each played twice) ──
        var locate = new List<(string group, CalStimulus s)>();
        void L(string g, params CalTone[] tones) => locate.Add((g, new CalStimulus(tones)));
        if (caps.SensaPalms)
        {
            L("palmL weak 120", new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .30, 120));
            L("palmL strong 200 pulse", new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .85, 200, CalTexture.Pulse));
            L("palmR mid 60", new CalTone(CalActuator.PalmRight, CalPath.Sensa, .55, 60));
            L("palmR mid 300 ramp", new CalTone(CalActuator.PalmRight, CalPath.Sensa, .55, 300, CalTexture.Ramp));
            L("palms both 120", new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .5, 120), new CalTone(CalActuator.PalmRight, CalPath.Sensa, .5, 120));
            L("palmL threshold 120", new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .08, 120));
        }
        if (caps.SensaTriggers || caps.RumbleTriggers)
        {
            L("trigL weak", new CalTone(CalActuator.TriggerLeft, trigPath, .25, 60));
            L("trigR strong pulse", new CalTone(CalActuator.TriggerRight, trigPath, .8, 60, CalTexture.Pulse));
            L("triggers both", new CalTone(CalActuator.TriggerLeft, trigPath, .6, 60), new CalTone(CalActuator.TriggerRight, trigPath, .6, 60));
            if (caps.SensaTriggers) L("trigL full p110", new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 110));
            if (caps.RumbleTriggers) L("trigR rumble mid", new CalTone(CalActuator.TriggerRight, CalPath.Rumble, .6));
        }
        if (caps.Body)
        {
            L("body big mid", new CalTone(CalActuator.BodyBig, CalPath.Rumble, .5));
            L("body small pulse", new CalTone(CalActuator.BodySmall, CalPath.Rumble, .7, 0, CalTexture.Pulse));
        }
        if (caps.SensaPalms && (caps.SensaTriggers || caps.RumbleTriggers))
            L("palmL + trigL", new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .5, 120), new CalTone(CalActuator.TriggerLeft, trigPath, .6, 60));
        if (caps.SensaPalms && caps.Body)
            L("palmR + body small", new CalTone(CalActuator.PalmRight, CalPath.Sensa, .5, 120), new CalTone(CalActuator.BodySmall, CalPath.Rumble, .5));
        locate.Add(("nothing", CalStimulus.Nothing));

        var trials = new List<CalTrial>();
        foreach (var (group, s) in locate.Concat(locate))
        {
            string correct = Label(s);
            var options = Distractors(s, available, rng).Prepend(correct).OrderBy(_ => rng.Next()).ToList();
            trials.Add(new CalTrial(CalTrialKind.Locate, "Nerede hissettin?", s, null, options, options.IndexOf(correct), group));
        }

        // ── Compare pairs (both orders) ──
        void Pair(CalTrialKind kind, string group, string nameA, CalStimulus a, string nameB, CalStimulus b, int strongerIsB = -1)
        {
            string q = kind == CalTrialKind.Stronger ? "Hangisi daha GÜÇLÜ?" : "Hangisi daha İYİ hissettirdi?";
            trials.Add(new CalTrial(kind, q, a, b, CompareOptions, strongerIsB < 0 ? -1 : strongerIsB, group, nameA, nameB, true));
            trials.Add(new CalTrial(kind, q, b, a, CompareOptions, strongerIsB < 0 ? -1 : 1 - strongerIsB, group, nameA, nameB, false));
        }
        static CalStimulus S(params CalTone[] t) => new(t, 1.4);
        if (caps.SensaTriggers && caps.RumbleTriggers)
        {
            Pair(CalTrialKind.Stronger, "trigger path", "Sensa tetik", S(new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 60)),
                "GameInput tetik", S(new CalTone(CalActuator.TriggerLeft, CalPath.Rumble, 1)));
            Pair(CalTrialKind.Better, "trigger path+both", "Sensa tetik", S(new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 60)),
                "Sensa + GameInput", S(new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 60), new CalTone(CalActuator.TriggerLeft, CalPath.Rumble, 1)));
        }
        if (caps.SensaTriggers)
        {
            Pair(CalTrialKind.Stronger, "trigger pitch", "tetik p0", S(new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 0)),
                "tetik p127", S(new CalTone(CalActuator.TriggerLeft, CalPath.Sensa, 1, 127)));
            Pair(CalTrialKind.Stronger, "trigger JND", "tetik 0.5", S(new CalTone(CalActuator.TriggerRight, CalPath.Sensa, .5, 60)),
                "tetik 0.75", S(new CalTone(CalActuator.TriggerRight, CalPath.Sensa, .75, 60)), strongerIsB: 1);
        }
        if (caps.SensaPalms)
        {
            Pair(CalTrialKind.Better, "palm pitch", "avuç 80 Hz", S(new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .6, 80)),
                "avuç 180 Hz", S(new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .6, 180)));
            Pair(CalTrialKind.Stronger, "palm JND", "avuç 0.45", S(new CalTone(CalActuator.PalmRight, CalPath.Sensa, .45, 120)),
                "avuç 0.6", S(new CalTone(CalActuator.PalmRight, CalPath.Sensa, .6, 120)), strongerIsB: 1);
        }
        if (caps.SensaPalms && caps.Body)
            Pair(CalTrialKind.Better, "haptic vs rumble", "avuç (Sensa)", S(new CalTone(CalActuator.PalmLeft, CalPath.Sensa, .6, 120)),
                "sap (gövde)", S(new CalTone(CalActuator.BodyBig, CalPath.Rumble, .6)));

        return trials.OrderBy(_ => rng.Next()).ToList();
    }

    /// <summary>Two wrong answers, preferring the ones a hand could confuse: the same side, the mirror, a mix with it.</summary>
    private static IEnumerable<string> Distractors(CalStimulus s, IReadOnlyList<CalActuator> available, Random rng)
    {
        string correct = Label(s);
        var acts = s.Tones.Select(t => t.Actuator).Distinct().ToList();
        var close = new List<string>();
        var far = new List<string>();
        foreach (var a in available)
        {
            var single = Label(a);
            bool related = acts.Count == 0 || acts.Any(x => x == Mirror(a) || IsLeft(x) == IsLeft(a));
            (related ? close : far).Add(single);
        }
        foreach (var a in acts)
            foreach (var b in available.Where(b => b != a && IsLeft(b) == IsLeft(a)))
                close.Add(Label(new CalStimulus(new[] { new CalTone(a, CalPath.Sensa, 1), new CalTone(b, CalPath.Sensa, 1) })));
        if (acts.Count > 1) close.AddRange(acts.Select(Label));
        if (acts.Count > 0 && rng.NextDouble() < .3) close.Add(Nothing);
        if (acts.Count == 0) close.Add(Label(available[rng.Next(available.Count)]));
        var picks = close.Where(o => o != correct).Distinct().OrderBy(_ => rng.Next()).ToList();
        picks.AddRange(far.Concat(available.Select(Label)).Append(Nothing).Where(o => o != correct && !picks.Contains(o)).Distinct().OrderBy(_ => rng.Next()));
        return picks.Take(2);
    }
}

/// <summary>Scores a calibration run and writes its raw data.</summary>
public static class CalibrationScoring
{
    public static string ToJsonLine(int index, CalResult r)
    {
        var t = r.Trial;
        return JsonSerializer.Serialize(new
        {
            index, kind = t.Kind.ToString(), group = t.Group, question = t.Question,
            first = t.First.Describe(), second = t.Second?.Describe(), firstLabel = CalibrationPlan.Label(t.First),
            options = t.Options, correct = t.Correct, answer = r.Answer,
            answerText = r.Answer >= 0 && r.Answer < t.Options.Count ? t.Options[r.Answer] : null,
            variantA = t.VariantA, variantB = t.VariantB, firstIsA = t.FirstIsA,
            responseMs = Math.Round(r.ResponseMs), replays = r.Replays
        });
    }

    public static string Summarize(IReadOnlyList<CalResult> results, CalCapabilities caps)
    {
        var sb = new StringBuilder();
        var loc = results.Where(r => r.Trial.Kind == CalTrialKind.Locate).ToList();
        int hit = loc.Count(r => r.Answer == r.Trial.Correct);
        sb.AppendLine($"Mod: {caps.Mode} · {results.Count} deneme · ort. yanıt {(results.Count > 0 ? results.Average(r => r.ResponseMs) / 1000 : 0):0.0} sn");
        sb.AppendLine($"KONUM: {hit}/{loc.Count} doğru ({Pct(hit, loc.Count)}); şans payı %33");

        // Per actuator: trials whose stimulus contains it.
        foreach (var a in Enum.GetValues<CalActuator>())
        {
            var with = loc.Where(r => r.Trial.First.Tones.Any(t => t.Actuator == a && t.Strength > .1)).ToList();
            if (with.Count == 0) continue;
            int ok = with.Count(r => r.Answer == r.Trial.Correct);
            sb.AppendLine($"  {CalibrationPlan.Label(a),-16} {ok}/{with.Count} ({Pct(ok, with.Count)})");
        }

        // Where it went → where it was felt.
        sb.AppendLine("NEREDEN → NEREYE (gönderilen → seçilen):");
        foreach (var g in loc.GroupBy(r => CalibrationPlan.Label(r.Trial.First)).OrderBy(g => g.Key))
        {
            var answers = g.GroupBy(r => r.Answer >= 0 ? r.Trial.Options[r.Answer] : "?").OrderByDescending(x => x.Count())
                .Select(x => $"{x.Key} ×{x.Count()}");
            sb.AppendLine($"  {g.Key} → {string.Join(", ", answers)}");
        }

        // Same stimulus twice: same answer?
        var repeats = loc.GroupBy(r => r.Trial.Group).Where(g => g.Count() >= 2).ToList();
        int same = repeats.Count(g => g.Select(r => r.Answer >= 0 ? r.Trial.Options[r.Answer] : "?").Distinct().Count() == 1);
        sb.AppendLine($"TUTARLILIK: aynı uyaranda aynı cevap {same}/{repeats.Count} ({Pct(same, repeats.Count)})");
        var thr = loc.Where(r => r.Trial.Group.Contains("threshold")).ToList();
        if (thr.Count > 0) sb.AppendLine($"EŞİK (çok zayıf avuç 0.08): {thr.Count(r => r.Answer == r.Trial.Correct)}/{thr.Count} hissedildi ve yeri doğru");
        var none = loc.Where(r => r.Trial.Group == "nothing").ToList();
        if (none.Count > 0) sb.AppendLine($"BOŞ DENEME: {none.Count(r => r.Answer == r.Trial.Correct)}/{none.Count} \"hiçbir şey\"");

        // Pairs.
        var pairs = results.Where(r => r.Trial.Kind != CalTrialKind.Locate).GroupBy(r => r.Trial.Group).ToList();
        if (pairs.Count > 0) sb.AppendLine("KARŞILAŞTIRMALAR (iki sırayla da çalındı):");
        foreach (var g in pairs)
        {
            var t0 = g.First().Trial;
            int a = g.Count(r => (r.Answer == 0 && r.Trial.FirstIsA) || (r.Answer == 1 && !r.Trial.FirstIsA));
            int b = g.Count(r => (r.Answer == 1 && r.Trial.FirstIsA) || (r.Answer == 0 && !r.Trial.FirstIsA));
            int eq = g.Count(r => r.Answer == 2);
            string what = t0.Kind == CalTrialKind.Stronger ? "güçlü" : "iyi";
            string verdict = a > b ? t0.VariantA! : b > a ? t0.VariantB! : "fark yok / kararsız";
            string known = t0.Correct >= 0 ? $" · doğru cevap {g.Count(r => r.Answer == r.Trial.Correct)}/{g.Count()}" : "";
            sb.AppendLine($"  {t0.VariantA} vs {t0.VariantB} ({what}): {t0.VariantA} {a}, {t0.VariantB} {b}, fark yok {eq} → {verdict}{known}");
        }
        return sb.ToString();
    }

    private static string Pct(int a, int n) => n == 0 ? "-" : $"%{100.0 * a / n:0}";
}
