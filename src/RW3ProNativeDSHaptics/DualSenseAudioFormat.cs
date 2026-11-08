using System;
using System.Reflection;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// The USB DualSense render endpoint (observed 2026-11-07, Windows 11 26320) only accepts:
///   - Exclusive: WAVEFORMATEXTENSIBLE PCM16, 4ch, 48 kHz, channel mask 0x33 (FL FR BL BR)
///   - Shared:    its own mix format (WAVEFORMATEXTENSIBLE Float32, 4ch, mask 0x33)
/// A plain 4ch PCM16 WAVEFORMATEX is rejected in BOTH modes (E_INVALIDARG / unsupported).
/// Rear-left (BL, channel index 2) and rear-right (BR, index 3) drive the haptic actuators.
/// </summary>
public static class DualSenseAudioFormat
{
    public const int QuadChannelMask = 0x33;    // SPEAKER_FRONT_LEFT|FRONT_RIGHT|BACK_LEFT|BACK_RIGHT

    private static readonly FieldInfo? MaskField =
        typeof(WaveFormatExtensible).GetField("dwChannelMask", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>4ch / 48 kHz / PCM16 WAVEFORMATEXTENSIBLE with the quad (0x33) channel mask.</summary>
    public static WaveFormatExtensible QuadPcm16()
    {
        // NAudio 2.2.1 has no channel-mask constructor and defaults 4ch to 0xF (FL FR FC LFE),
        // which would route "channels 3/4" to centre/LFE instead of the rear haptic pair.
        object format = new WaveFormatExtensible(48000, 16, 4);
        if (MaskField == null)
            throw new InvalidOperationException("NAudio WaveFormatExtensible layout changed; cannot set quad channel mask.");
        MaskField.SetValue(format, QuadChannelMask);
        var result = (WaveFormatExtensible)format;
        if (ChannelMask(result) != QuadChannelMask)
            throw new InvalidOperationException("Quad channel mask could not be applied.");
        return result;
    }

    /// <summary>Unchanged reference PCM; unsupported Exclusive playback fails before output opens.</summary>
    public static RawSourceWaveStream OpenReferenceStream(byte[] pcm16, bool exclusiveSupported)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (pcm16.Length == 0 || pcm16.Length % 8 != 0)
            throw new ArgumentException("Referans PCM, tam 4 kanallı PCM16 çerçeveleri içermeli.", nameof(pcm16));
        if (!exclusiveSupported)
            throw new InvalidOperationException(
                "DualSense referans testi için Exclusive 4 kanal / 48 kHz PCM16 desteklenmiyor. " +
                "Shared moda otomatik geçilmedi; referans testi başlatılmadı.");
        return new RawSourceWaveStream(new System.IO.MemoryStream(pcm16, writable: false), QuadPcm16());
    }

    public static int ChannelMask(WaveFormatExtensible format) =>
        MaskField?.GetValue(format) is int mask ? mask : -1;
}
