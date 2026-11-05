namespace RW3ProNativeDSHaptics;

/// <summary>
/// Transport-level evidence, channel by channel. A successful API call alone is not
/// physical haptic confirmation. Native requires genuine game PCM + Razer output.
/// </summary>
public enum HapticTestChannel { Left, Right }

public sealed record ChannelTestResult(
    HapticMode Mode,
    HapticTestChannel Channel,
    bool TestSignalEmitted,
    bool SelectedChannelInputObserved,
    bool DeviceCommandAcknowledged,
    bool PhysicalSensationConfirmed,
    string Detail)
{
    public bool SignalPathObserved =>
        TestSignalEmitted && SelectedChannelInputObserved && DeviceCommandAcknowledged;

    public string Summary => Mode switch
    {
        HapticMode.Normal =>
            !DeviceCommandAcknowledged ? "XInput komutu reddedildi veya cihaz yok" :
            PhysicalSensationConfirmed ? "Kullanıcı klasik rumble titreşimini hissetti" :
                "XInput komutu kabul edildi; fiziksel his doğrulanmadı",
        HapticMode.Audio =>
            !SelectedChannelInputObserved ? "Test sesi sol/sağ WASAPI kanalında doğrulanmadı" :
            !DeviceCommandAcknowledged ? "Ses algılandı; XInput komutu doğrulanmadı" :
            PhysicalSensationConfirmed ? "Ses → XInput rumble yolu ve kullanıcı hissi doğrulandı (HD değil)" :
                "Ses → XInput komutu doğrulandı; fiziksel his doğrulanmadı",
        _ => "Native DualSense testi YAPILAMADI: gerçek oyun PCM yakalayıcısı ve Razer stereo çıkışı yok"
    };
}

public static class ChannelTestGate
{
    // Explicit capability gate; no fake native fallback and no synthetic DualSense source.
    public static bool CanRunNative(bool actualGamePcmCaptureReady, bool razerStereoSinkReady) =>
        actualGamePcmCaptureReady && razerStereoSinkReady;

    public static bool CanConfirmNative(
        bool actualGamePcmObserved, bool independentLeftRightOutputAcknowledged,
        bool physicalLeftConfirmed, bool physicalRightConfirmed) =>
        actualGamePcmObserved && independentLeftRightOutputAcknowledged &&
        physicalLeftConfirmed && physicalRightConfirmed;
}
