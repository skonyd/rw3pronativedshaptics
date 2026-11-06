using System;
using System.Collections.Generic;
using System.Linq;

namespace RW3ProNativeDSHaptics;

// A preference is never evidence that external hardware or game PCM is operational.
public enum HapticRoute
{
    NormalXInput, LocalSystemAudioXInput, NativeManualUac, NativePadForge,
    NativeDsxPlus, NativeUsbDualSense, OfficialRazerSensaGames,
    WwiseStereoMotionLab, OfficialRazerAudioToHaptics
}
public enum RouteReadiness { ReadyInApp, ExperimentalInApp, ExternalOnly, ResearchOnly }

public sealed record HapticRouteOption(
    HapticRoute Route, string DisplayName, RouteReadiness Readiness, HapticMode Mode,
    string Source, string Output, string Guidance, string ReferenceUrl,
    bool OriginalDualSenseSourcePossible, bool RecommendedForOriginalSource)
{
    public bool IsNative => Mode == HapticMode.Native;
    public bool CanStartInsideApp => Readiness is RouteReadiness.ReadyInApp or RouteReadiness.ExperimentalInApp;
    public string StageLabel => Readiness switch
    {
        RouteReadiness.ReadyInApp => "UYGULAMADA KULLANILABİLİR",
        RouteReadiness.ExperimentalInApp => "DENEYSEL • HARİCÎ AYGIT/SDK GEREKEBİLİR",
        RouteReadiness.ExternalOnly => "HARİCÎ UYGULAMADA YAPILANDIRILIR",
        _ => "ARAŞTIRMA • RW3 ENTEGRASYONU YOK"
    };
}
public static class HapticRouteCatalog
{
    public const HapticRoute RecommendedOriginalSource = HapticRoute.NativePadForge;
    private static readonly IReadOnlyList<HapticRouteOption> Routes = Array.AsReadOnly(new[]
    {
        new HapticRouteOption(HapticRoute.NativePadForge,
            "★ Özgün kaynak için öneri: PadForge Full → RW3 → Sensa",
            RouteReadiness.ExperimentalInApp, HapticMode.Native,
            "PadForge Full sanal DualSense 4ch/48k UAC",
            "Interhaptics sol/sağ parametrik 50 Hz Sensa (kayıplı)",
            "Öneri gerçek oyun DualSense PCM kaynağı potansiyeli içindir; fiziksel sadakat kanıtlanmadı. " +
            "PadForge kurulumu, Wolverine giriş eşlemesi ve çift girişten kaçınma kullanıcı sorumluluğundadır.",
            "https://padforge.org/docs/faq/", true, true),
        new HapticRouteOption(HapticRoute.NativeUsbDualSense,
            "Gerçek USB DualSense UAC → RW3 → Sensa",
            RouteReadiness.ExperimentalInApp, HapticMode.Native,
            "Kablolu DualSense 4ch/48k ses aygıtı",
            "Interhaptics sol/sağ parametrik Sensa (kayıplı)",
            "Oyunun gerçekten bu USB aygıtına haptic audio göndermesi gerekir. " +
            "DualSense'i bağlamak tek başına Wolverine'a aktarım sağlamaz; giriş çakışmasına dikkat.",
            "https://padforge.org/docs/faq/", true, false),
        new HapticRouteOption(HapticRoute.NativeDsxPlus,
            "DSX+ sanal DualSense Audio → RW3 → Sensa",
            RouteReadiness.ExperimentalInApp, HapticMode.Native,
            "DSX+ Virtual DualSense Audio 4ch/48k (uygun sürüm)",
            "Interhaptics sol/sağ parametrik Sensa (kayıplı)",
            "DSX+ sanal DualSense ses desteği mevcut, ancak Wolverine'ın DSX+ girişi olarak " +
            "çalışması doğrulanmadı. DSX+ ve sürücüsünü RW3 kurmaz veya başlatmaz.",
            "https://steamcommunity.com/app/1812620/announcements/", true, false),
        new HapticRouteOption(HapticRoute.NativeManualUac,
            "Elle seçilen 4ch UAC → RW3 → Sensa",
            RouteReadiness.ExperimentalInApp, HapticMode.Native,
            "Kullanıcının doğruladığı 4ch/48k PCM16/Float32 render aygıtı",
            "Interhaptics sol/sağ parametrik Sensa (kayıplı)",
            "Gelişmiş yöntem. Dört kanal görünmesi gerçek oyun DualSense kaynağını ispatlamaz.",
            "https://github.com/skonyd/rw3pronativedshaptics/blob/main/docs/DEVICE_TEST_RUNBOOK.md", true, false),
        new HapticRouteOption(HapticRoute.OfficialRazerSensaGames,
            "Razer Sensa HD Games • resmî oyun desteği",
            RouteReadiness.ExternalOnly, HapticMode.Normal,
            "Oyunun Razer için hazırladığı Sensa efektleri (Sony DualSense PCM değil)",
            "Synapse/Chroma → Wolverine Sensa; RW3 ses/motor göndermez",
            "Uygun destekli oyunlar için en güvenilir Sensa uygulama yolu. " +
            "Synapse 4'te Sensa HD Games kullan; Sony'nin özgün haptics'ini aktarmıyor.",
            "https://www.razer.com/synapse-4", false, false),
        new HapticRouteOption(HapticRoute.OfficialRazerAudioToHaptics,
            "Razer Synapse Audio-to-Haptics • resmî",
            RouteReadiness.ExternalOnly, HapticMode.Normal,
            "Windows sistem/oyun sesi (DualSense PCM değil)",
            "Synapse → Wolverine Sensa; RW3 audio capture kapalı",
            "Synapse 4'te Audio-to-Haptics'i seç. Aynı anda RW3'nin kendi audio modunu çalıştırma.",
            "https://www.razer.com/synapse-4", false, false),
        new HapticRouteOption(HapticRoute.LocalSystemAudioXInput,
            "RW3 sistem sesi → standart XInput rumble",
            RouteReadiness.ReadyInApp, HapticMode.Audio,
            "WASAPI varsayılan Windows sesi",
            "XInput standart titreşim; Sensa HD değil",
            "Yalnızca açık izinle WASAPI capture açılır. Gerçek DualSense PCM taşınmaz.",
            "https://github.com/skonyd/rw3pronativedshaptics", false, false),
        new HapticRouteOption(HapticRoute.NormalXInput,
            "Normal Rumble • oyun XInput'u yönetir",
            RouteReadiness.ReadyInApp, HapticMode.Normal,
            "Oyunun normal gamepad geri bildirimi", "Oyunun XInput titreşimi",
            "RW3 oyun titreşimini yakalamaz veya değiştirmez; en az müdahaleli mod.",
            "https://github.com/skonyd/rw3pronativedshaptics", false, false),
        new HapticRouteOption(HapticRoute.WwiseStereoMotionLab,
            "Wwise Sensa stereo Motion • ayrı araştırma",
            RouteReadiness.ResearchOnly, HapticMode.Normal,
            "Wwise içe aktarılmış WAV/.haptic; canlı DualSense oyunu değil",
            "Wwise Motion → Sensa (RW3 içinde canlı entegrasyon YOK)",
            "Resmî Wwise Motion/WYVRN eklentisi ve lisansı ayrı gerekir. " +
            "Bu seçim WAV oynatmaz veya Wwise başlatmaz. Canlı PCM API'si kanıtlanmış değil.",
            "https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/",
            false, false)
    });

    public static IReadOnlyList<HapticRouteOption> All => Routes;
    public static HapticRouteOption Get(HapticRoute route) =>
        Routes.FirstOrDefault(x => x.Route == route) ??
        Routes.First(x => x.Route == HapticRoute.NormalXInput);
    public static HapticRoute DefaultForMode(HapticMode mode) => mode switch
    {
        HapticMode.Audio => HapticRoute.LocalSystemAudioXInput,
        HapticMode.Native => HapticRoute.NativeManualUac,
        _ => HapticRoute.NormalXInput
    };
    public static bool IsNative(HapticRoute route) => Get(route).Route == route && Get(route).IsNative;

    // A suggested friendly-name match is never device identity or game provenance proof.
    public static bool NameMayMatch(HapticRoute route, string? name)
    {
        if (route == HapticRoute.NativeManualUac) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.ToLowerInvariant();
        return route switch
        {
            HapticRoute.NativePadForge => n.Contains("padforge") || n.Contains("dualsense"),
            HapticRoute.NativeDsxPlus => n.Contains("dsx") || n.Contains("dualsense"),
            HapticRoute.NativeUsbDualSense => n.Contains("dualsense") || n.Contains("wireless controller"),
            _ => false
        };
    }
    public static bool CanStartBridge(HapticRoute route, bool endpointCompatible, bool userAcknowledged) =>
        IsNative(route) && endpointCompatible && userAcknowledged;
}
