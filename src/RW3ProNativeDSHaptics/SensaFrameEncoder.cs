using System;

namespace RW3ProNativeDSHaptics;

/// <summary>One band of one hand: 4 strength points (0..1) and one pitch (0..1 over 30–400 Hz).</summary>
public readonly record struct SensaBand(double A0, double A1, double A2, double A3, double Pitch)
{
    public static SensaBand Silent(double pitch) => new(0, 0, 0, 0, pitch);
}

/// <summary>
/// Builds the Wolverine V3 Pro Sensa frame without HAR/Synapse (decoded 2026-11-08, docs/SENSA_USB_PROTOCOL.md).
///
/// The frame is one MSB-first bit stream: 7-bit header 0111100, then per hand (left, right):
/// 3 bands × ([1] + 4 points × (strength 6 bit = ⌊a·63⌋, pitch 7 bit = ⌊p·127⌋)) and 1 bit
/// "transient record follows" (0 here); padded to 41 bytes. p is 0..1 linear over 30–400 Hz.
/// Band 1 is the plain signal; bands 2–3 carry extra spectral components (what HAR's multi-melody
/// .haps produce). Verified byte-exact against 1696 HAR-rendered frames (single- and multi-band).
/// Not written: transient records (24 bits: strength 6, pitch 7, time 8 in 0.5 ms, 3 bits not yet
/// understood) — hits are folded into the strength points instead.
/// </summary>
public static class SensaFrameEncoder
{
    public const int FrameLength = 41, BlockLength = 19, Points = 4;
    public const byte Header = 0x79, Separator = 0x01;
    public const double MinHz = 30, MaxHz = 400;

    /// <summary>Pitch value 0..1 for a frequency (linear over 30–400 Hz, as the HAR effects use it).</summary>
    public static double PitchOf(double hz) => Math.Clamp((hz - MinHz) / (MaxHz - MinHz), 0, 1);

    public static byte Quantize6(double a) => (byte)(double.IsFinite(a) ? Math.Clamp((int)Math.Floor(Math.Clamp(a, 0, 1) * 63), 0, 63) : 0);
    public static byte Quantize7(double p) => (byte)(double.IsFinite(p) ? Math.Clamp((int)Math.Floor(Math.Clamp(p, 0, 1) * 127), 0, 127) : 0);

    /// <summary>Encodes a full frame with up to 3 bands per hand (missing bands are silent).</summary>
    public static void EncodeBands(ReadOnlySpan<SensaBand> left, ReadOnlySpan<SensaBand> right, Span<byte> frame)
    {
        if (left.Length > 3 || right.Length > 3) throw new ArgumentException("at most 3 bands per hand");
        frame.Slice(0, FrameLength).Clear();
        int bit = 0;
        Put(frame, ref bit, 0b0111100, 7);
        WriteHand(frame, ref bit, left);
        WriteHand(frame, ref bit, right);
    }

    private static void WriteHand(Span<byte> frame, ref int bit, ReadOnlySpan<SensaBand> bands)
    {
        for (int i = 0; i < 3; i++)
        {
            Put(frame, ref bit, 1, 1);
            if (i < bands.Length)
            {
                var b = bands[i];
                byte p = Quantize7(b.Pitch);
                Put(frame, ref bit, Quantize6(b.A0), 6); Put(frame, ref bit, p, 7);
                Put(frame, ref bit, Quantize6(b.A1), 6); Put(frame, ref bit, p, 7);
                Put(frame, ref bit, Quantize6(b.A2), 6); Put(frame, ref bit, p, 7);
                Put(frame, ref bit, Quantize6(b.A3), 6); Put(frame, ref bit, p, 7);
            }
            else bit += 52;   // silent band: strength 0, pitch 0
        }
        Put(frame, ref bit, 0, 1);   // no transient record
    }

    /// <summary>Encodes one block from 4 strengths and 4 pitches (0..1).</summary>
    public static void EncodeBlock(ReadOnlySpan<double> amps, ReadOnlySpan<double> pitches, Span<byte> block)
    {
        block.Slice(0, BlockLength).Clear();
        int bit = 0;
        for (int i = 0; i < Points; i++) { Put(block, ref bit, Quantize6(amps[i]), 6); Put(block, ref bit, Quantize7(pitches[i]), 7); }
        Put(block, ref bit, 1, 1);   // bit 52
        block[13] |= 0x40;
    }

    private static void Put(Span<byte> block, ref int bit, int value, int width)
    {
        for (int k = width - 1; k >= 0; k--, bit++)
            if (((value >> k) & 1) != 0) block[bit >> 3] |= (byte)(0x80 >> (bit & 7));
    }

    /// <summary>Full plain frame: left then right hand, constant pitch per hand.</summary>
    public static void Encode(ReadOnlySpan<double> leftAmps, double leftPitch, ReadOnlySpan<double> rightAmps, double rightPitch, Span<byte> frame)
    {
        Span<double> lp = stackalloc double[Points], rp = stackalloc double[Points];
        lp.Fill(leftPitch); rp.Fill(rightPitch);
        frame[0] = Header;
        EncodeBlock(leftAmps, lp, frame.Slice(1, BlockLength));
        frame[1 + BlockLength] = Separator;
        EncodeBlock(rightAmps, rp, frame.Slice(2 + BlockLength, BlockLength));
        frame[FrameLength - 1] = 0;
    }

    /// <summary>Silence frame (both hands zero; pitch matches what Synapse sends at rest).</summary>
    public static byte[] Silence()
    {
        var f = new byte[FrameLength];
        Span<double> z = stackalloc double[Points];
        Encode(z, PitchOf(50.0), z, PitchOf(50.0), f);
        return f;
    }

    /// <summary>Decodes a plain block (diagnostics/tests).</summary>
    public static (byte[] Amps, byte[] Pitches) DecodeBlock(ReadOnlySpan<byte> block)
    {
        var a = new byte[Points]; var p = new byte[Points]; int bit = 0;
        for (int i = 0; i < Points; i++) { a[i] = (byte)Get(block, ref bit, 6); p[i] = (byte)Get(block, ref bit, 7); }
        return (a, p);
    }

    private static int Get(ReadOnlySpan<byte> b, ref int bit, int width)
    {
        int v = 0;
        for (int k = 0; k < width; k++, bit++) v = (v << 1) | ((b[bit >> 3] >> (7 - (bit & 7))) & 1);
        return v;
    }
}

/// <summary>Feature → strength-point mapping of the Direct USB path (pure; shared with tests).</summary>
public static class DirectSensaMapping
{
    /// <summary>4 strength points for one hand (limiter applied); transients raise the nearest point. False = silent.</summary>
    public static bool Points(in SideFeatures f, double gain, double[] a)
    {
        a[0] = HapticLimiter.Apply(f.P0, gain); a[1] = HapticLimiter.Apply(f.P1, gain);
        a[2] = HapticLimiter.Apply(f.P2, gain); a[3] = HapticLimiter.Apply(f.P3, gain);
        if (f.TransientCount >= 1) Fold(a, f.T0Position, HapticLimiter.Apply(f.T0Amplitude, gain));
        if (f.TransientCount >= 2) Fold(a, f.T1Position, HapticLimiter.Apply(f.T1Amplitude, gain));
        bool on = false;
        for (int i = 0; i < 4; i++) on |= SensaFrameEncoder.Quantize6(a[i]) > 0;
        return on;
        static void Fold(double[] a, double pos, double amp)
        {
            int i = Math.Clamp((int)Math.Round(pos * 3), 0, 3);
            a[i] = Math.Max(a[i], amp);
        }
    }

}
