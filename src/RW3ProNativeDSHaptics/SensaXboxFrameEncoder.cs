using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Sensa frame for the Wolverine V3 Pro in <b>Xbox mode</b>: 4 body parts in one frame, the two palms (216, 116) and the
/// two trigger motors (249, 149). Decoded 2026-11-11 by emulating the firmware 2.04.00 decoder; the body-part map was
/// confirmed by a blind hand-held test (docs/SENSA_USB_PROTOCOL.md "Xbox mode frame", docs/research/EXPERIMENTS.md
/// E-20261111-01/03/04).
///
/// MSB-first bit stream: 7-bit header 0111100, then per body part in order left palm, right palm, left trigger,
/// right trigger:
/// <list type="bullet">
/// <item>a band list of up to 2 bands, each <c>1</c> + 2 × (strength 6, pitch 7), ended early by a <c>0</c>;</item>
/// <item>a transient list of up to 2 records (palms) or 1 (triggers), each <c>1</c> + strength 6 + pitch 7 + time 7 (ms),
/// ended early by a <c>0</c>.</item>
/// </list>
/// Palms use the PC pitch scale (p = 0..1 over 30–400 Hz). The trigger motors take strength only (they speed up with it).
/// Their pitch field is raw and its effect is not measured, so a fixed value is sent.
/// </summary>
public static class SensaXboxFrameEncoder
{
    /// <summary>All 4 body parts with 2 bands and their maximum transients: 349 bits.</summary>
    public const int MaxFrameLength = 44;
    /// <summary>Pitch written for a silent band (the PC silence frame uses the same value).</summary>
    public const int SilentPitch = 6;
    /// <summary>Raw pitch field for an active trigger motor (the value used in the hardware tests).</summary>
    public const int TriggerPitch = 60;
    private const int Header = 0b0111100, PalmHits = 2;

    /// <summary>Encodes one frame; returns its length in bytes. Palms take up to 2 bands each; a PC band's 4 points
    /// fold into 2 (the larger of each half), so short peaks keep their strength.</summary>
    public static int Encode(ReadOnlySpan<SensaBand> left, ReadOnlySpan<SensaBand> right, SensaTransient? leftHit,
        SensaTransient? rightHit, double triggerLeft, double triggerRight, Span<byte> frame, int triggerPitch = TriggerPitch)
    {
        if (frame.Length < MaxFrameLength) throw new ArgumentException("frame buffer too small");
        frame.Slice(0, MaxFrameLength).Clear();
        int bit = 0;
        Put(frame, ref bit, Header, 7);
        Palm(frame, ref bit, left, leftHit);
        Palm(frame, ref bit, right, rightHit);
        Trigger(frame, ref bit, triggerLeft, triggerPitch);
        Trigger(frame, ref bit, triggerRight, triggerPitch);
        return (bit + 7) / 8;
    }

    /// <summary>Every body part silent (one zero band each).</summary>
    public static byte[] Silence()
    {
        var f = new byte[MaxFrameLength];
        int n = Encode(ReadOnlySpan<SensaBand>.Empty, ReadOnlySpan<SensaBand>.Empty, null, null, 0, 0, f);
        return f.AsSpan(0, n).ToArray();
    }

    public const int GipPacketLength = 60;

    /// <summary>Wraps one frame as the Razer command (class 0x16, cmd 0x0E, args [01, frame]) in the 60-byte payload of GIP
    /// message 0x0F: [status, tx, 0, 0, 0, size, class, cmd, args…], XOR of bytes 2..57 at byte 58 (captured from Razer
    /// Controller Setup for Xbox, 2026-11-10). Pure; used by <see cref="GipSensaTransport"/> and the tests.</summary>
    public static void BuildGipPacket(ReadOnlySpan<byte> frame, byte tx, Span<byte> packet)
    {
        if (frame.Length == 0 || frame.Length > 47) throw new ArgumentException("frame length " + frame.Length);
        packet.Slice(0, GipPacketLength).Clear();
        packet[1] = tx; packet[5] = (byte)(frame.Length + 1); packet[6] = 0x16; packet[7] = 0x0E; packet[8] = 0x01;
        frame.CopyTo(packet.Slice(9));
        byte x = 0;
        for (int i = 2; i < 58; i++) x ^= packet[i];
        packet[58] = x;
    }

    private static void Palm(Span<byte> f, ref int bit, ReadOnlySpan<SensaBand> bands, SensaTransient? hit)
    {
        int n = Math.Min(2, bands.Length);
        if (n == 0) Band(f, ref bit, 0, 0, SilentPitch);
        for (int i = 0; i < n; i++)
        {
            var b = bands[i];
            Band(f, ref bit, SensaFrameEncoder.Quantize6(Math.Max(b.A0, b.A1)), SensaFrameEncoder.Quantize6(Math.Max(b.A2, b.A3)),
                SensaFrameEncoder.Quantize7(b.Pitch));
        }
        if (Math.Max(1, n) < 2) Put(f, ref bit, 0, 1);   // end of the band list
        if (hit is { } h && SensaFrameEncoder.Quantize6(h.Strength) > 0)
        {
            Put(f, ref bit, 1, 1);
            Put(f, ref bit, SensaFrameEncoder.Quantize6(h.Strength), 6);
            Put(f, ref bit, SensaFrameEncoder.TransientType, 7);
            Put(f, ref bit, SensaFrameEncoder.TransientTime(h.Position), 7);
            if (PalmHits > 1) Put(f, ref bit, 0, 1);       // end of the record list
        }
        else Put(f, ref bit, 0, 1);
    }

    /// <summary>Lowest strength the trigger motors are felt at: 8/63 was not felt, 16/63 was (threshold test, pulled, 2026-11-11).</summary>
    public const int TriggerFloor = 16;

    /// <summary>Trigger strength 0..1 → raw 6-bit value: 0 stays 0, anything above starts at <see cref="TriggerFloor"/> so weak
    /// effects (DualSense resistance zones at a fraction of full force) are still felt.</summary>
    public static int TriggerLevel(double strength)
    {
        if (!double.IsFinite(strength) || strength <= 0.01) return 0;
        return (int)Math.Round(TriggerFloor + Math.Clamp(strength, 0, 1) * (63 - TriggerFloor));
    }

    private static void Trigger(Span<byte> f, ref int bit, double strength, int pitch)
    {
        int s = TriggerLevel(strength);
        Band(f, ref bit, s, s, s > 0 ? Math.Clamp(pitch, 0, 127) : SilentPitch);
        Put(f, ref bit, 0, 1);   // end of the band list
        Put(f, ref bit, 0, 1);   // no transient
    }

    private static void Band(Span<byte> f, ref int bit, int a0, int a1, int pitch)
    {
        Put(f, ref bit, 1, 1);
        Put(f, ref bit, a0, 6); Put(f, ref bit, pitch, 7);
        Put(f, ref bit, a1, 6); Put(f, ref bit, pitch, 7);
    }

    private static void Put(Span<byte> f, ref int bit, int value, int width)
    {
        for (int k = width - 1; k >= 0; k--, bit++)
            if (((value >> k) & 1) != 0) f[bit >> 3] |= (byte)(0x80 >> (bit & 7));
    }
}
