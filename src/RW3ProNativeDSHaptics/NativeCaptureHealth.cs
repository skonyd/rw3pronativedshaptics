using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Pure signal provenance state. A 4-channel endpoint, nonzero PCM or provider
/// render counter does not prove that a real game authored DualSense haptics.
/// </summary>
public enum NativeCaptureState
{
    Stopped,
    WaitingForAudio,
    AudioSilentOrSpeakerOnly,
    RearChannelEnergyObserved,
    AudioStale
}

public static class NativeCaptureHealth
{
    public const long RecentWindowMs = 2500;

    public static NativeCaptureState Evaluate(
        bool capturing, long lastFrameTick, long lastLeftEnergyTick,
        long lastRightEnergyTick, long nowTick)
    {
        if (!capturing) return NativeCaptureState.Stopped;
        if (lastFrameTick <= 0) return NativeCaptureState.WaitingForAudio;
        if (nowTick < lastFrameTick || nowTick - lastFrameTick >= RecentWindowMs)
            return NativeCaptureState.AudioStale;
        return HapticSafetyRules.HasRecentChannelEnergy(
            true, lastLeftEnergyTick, lastRightEnergyTick, nowTick)
            ? NativeCaptureState.RearChannelEnergyObserved
            : NativeCaptureState.AudioSilentOrSpeakerOnly;
    }

    public static string Describe(NativeCaptureState state) => state switch
    {
        NativeCaptureState.Stopped => "DURDU • herhangi bir ses kaynağı gözlenmiyor",
        NativeCaptureState.WaitingForAudio => "BEKLİYOR • 4-kanal UAC açık, henüz veri gelmedi",
        NativeCaptureState.AudioSilentOrSpeakerOnly =>
            "ÖN SES/SESSİZ • taze UAC çerçevesi var, arka haptic kanalı aktif değil",
        NativeCaptureState.RearChannelEnergyObserved =>
            "ARKA KANAL PCM GÖZLENDİ • oyun kaynaklı olduğu doğrulanmadı",
        _ => "ESKİ VERİ • ses paketleri durdu/koptu, önceki RMS geçerli değil"
    };
}
