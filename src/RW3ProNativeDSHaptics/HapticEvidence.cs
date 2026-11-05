namespace RW3ProNativeDSHaptics;

/// <summary>
/// Truth-in-telemetry gate: a selected mode or accepted XInput command cannot
/// be labeled as native DualSense hardware playback.
/// Keep this decision pure so CI can test it without a controller.
/// </summary>
public static class HapticEvidence
{
    public static string Evaluate(
        HapticMode mode,
        bool recentSystemAudio,
        bool recentXInputAcknowledgment,
        bool nativeStereoCaptured,
        bool nativeStereoOutputAcknowledged,
        bool physicalNativePlaybackConfirmed)
    {
        if (mode == HapticMode.Normal)
            return "NORMAL RUMBLE: oyun tarafından yönetiliyor, bu uygulama oyun titreşimini izlemiyor";

        if (mode == HapticMode.Audio)
        {
            if (!recentSystemAudio) return "SİSTEM SESİ: ses paketi bekleniyor (DualSense PCM değil)";
            return recentXInputAcknowledgment
                ? "SİSTEM SESİ: ses alındı ve XInput komutu kabul edildi (fiziksel HD teyidi değil)"
                : "SİSTEM SESİ: ses alındı, XInput çıkışı doğrulanmadı (DualSense PCM değil)";
        }

        if (!nativeStereoCaptured)
            return "NATIVE DUALSENSE: PCM kaynağı doğrulanmadı; HD efekt aktif DEĞİL";
        if (!nativeStereoOutputAcknowledged)
            return "NATIVE DUALSENSE: PCM yakalandı, Wolverine HD çıkışı doğrulanmadı";
        if (!physicalNativePlaybackConfirmed)
            return "NATIVE DUALSENSE: dijital çıktı kabul edildi, fiziksel HD oynatma doğrulanmadı";
        return "NATIVE DUALSENSE: stereo kaynak, çıkış ve fiziksel geri bildirim doğrulandı";
    }
}
