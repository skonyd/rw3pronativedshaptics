using System;

namespace RW3ProNativeDSHaptics;

/// <summary>One band of one hand: 4 strength points (0..1) and one pitch (0..1 over 30–400 Hz).</summary>
public readonly record struct SensaBand(double A0, double A1, double A2, double A3, double Pitch)
{
    public static SensaBand Silent(double pitch) => new(0, 0, 0, 0, pitch);
}

/// <summary>One hit (transient record) in a hand's frame: strength 0..1, position 0..1 within the 33 ms frame.</summary>
public readonly record struct SensaTransient(double Strength, double Position);

/// <summary>
/// Builds the Wolverine V3 Pro Sensa frame without HAR/Synapse (decoded 2026-11-08, docs/SENSA_USB_PROTOCOL.md).
///
/// The frame is one MSB-first bit stream: 7-bit header 0111100, then per hand (left, right):
/// 3 bands × ([1] + 4 points × (strength 6 bit = ⌊a·63⌋, pitch 7 bit = ⌊p·127⌋)) and 1 bit
/// "transient record follows" (0 here); padded to 41 bytes. p is 0..1 linear over 30–400 Hz.
/// Band 1 is the plain signal; bands 2–3 carry extra spectral components (what HAR's multi-melody
/// .haps produce). Verified byte-exact against 1696 HAR-rendered frames (single- and multi-band).
/// Transient record (decoded 2026-11-09 from HAR renders): strength 6 = ⌊a·63⌋, a constant 7-bit field 24,
/// time 8 = 2·round(position·33) (whole ms, 0.5 ms units), tail 3 = 100. One record per hand at most
/// (frame 44 bytes with one, 47 with two). HAR does not fold the hit into the strength points.
/// </summary>
public static class SensaFrameEncoder
{
    public const int FrameLength = 41, BlockLength = 19, Points = 4;
    /// <summary>Largest frame: one transient record per hand.</summary>
    public const int MaxFrameLength = 47;
    public const int TransientType = 24, TransientTail = 0b100;

    /// <summary>Transient time field: whole milliseconds within the 33 ms frame, in 0.5 ms units.</summary>
    public static int TransientTime(double position) =>
        2 * (int)Math.Round(Math.Clamp(double.IsFinite(position) ? position : 0, 0, 1) * 33, MidpointRounding.AwayFromZero);
    public const byte Header = 0x79, Separator = 0x01;
    public const double MinHz = 30, MaxHz = 400;

    /// <summary>Pitch value 0..1 for a frequency (linear over 30–400 Hz, as the HAR effects use it).</summary>
    public static double PitchOf(double hz) => Math.Clamp((hz - MinHz) / (MaxHz - MinHz), 0, 1);

    public static byte Quantize6(double a) => (byte)(double.IsFinite(a) ? Math.Clamp((int)Math.Floor(Math.Clamp(a, 0, 1) * 63), 0, 63) : 0);
    public static byte Quantize7(double p) => (byte)(double.IsFinite(p) ? Math.Clamp((int)Math.Floor(Math.Clamp(p, 0, 1) * 127), 0, 127) : 0);

    /// <summary>Encodes a full frame with up to 3 bands per hand (missing bands are silent).</summary>
    public static void EncodeBands(ReadOnlySpan<SensaBand> left, ReadOnlySpan<SensaBand> right, Span<byte> frame) =>
        EncodeBands(left, right, frame, null, null);

    /// <summary>Encodes a frame with optional hit records; returns its length (41, 44 or 47 bytes).</summary>
    public static int EncodeBands(ReadOnlySpan<SensaBand> left, ReadOnlySpan<SensaBand> right, Span<byte> frame,
        SensaTransient? leftHit, SensaTransient? rightHit)
    {
        if (left.Length > 3 || right.Length > 3) throw new ArgumentException("at most 3 bands per hand");
        if (leftHit is { } lh && Quantize6(lh.Strength) == 0) leftHit = null;
        if (rightHit is { } rh && Quantize6(rh.Strength) == 0) rightHit = null;
        int length = FrameLength + 3 * ((leftHit != null ? 1 : 0) + (rightHit != null ? 1 : 0));
        if (frame.Length < length) throw new ArgumentException("frame buffer too small");
        frame.Slice(0, length).Clear();
        int bit = 0;
        Put(frame, ref bit, 0b0111100, 7);
        WriteHand(frame, ref bit, left, leftHit);
        WriteHand(frame, ref bit, right, rightHit);
        return length;
    }

    private static void WriteHand(Span<byte> frame, ref int bit, ReadOnlySpan<SensaBand> bands, SensaTransient? hit = null)
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
        if (hit is { } h)
        {
            Put(frame, ref bit, 1, 1);   // transient record follows
            Put(frame, ref bit, Quantize6(h.Strength), 6);
            Put(frame, ref bit, TransientType, 7);
            Put(frame, ref bit, TransientTime(h.Position), 8);
            Put(frame, ref bit, TransientTail, 3);
        }
        else Put(frame, ref bit, 0, 1);   // no transient record
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
    /// <param name="recordFirstHit">The first hit goes into a transient record (see <see cref="Hit"/>), not the points.</param>
    public static bool Points(in SideFeatures f, double gain, double[] a, bool recordFirstHit = false)
    {
        a[0] = HapticLimiter.Apply(f.P0, gain); a[1] = HapticLimiter.Apply(f.P1, gain);
        a[2] = HapticLimiter.Apply(f.P2, gain); a[3] = HapticLimiter.Apply(f.P3, gain);
        if (f.TransientCount >= 1 && !recordFirstHit) Fold(a, f.T0Position, HapticLimiter.Apply(f.T0Amplitude, gain));
        if (f.TransientCount >= 2) Fold(a, f.T1Position, HapticLimiter.Apply(f.T1Amplitude, gain));
        bool on = recordFirstHit && Hit(f, gain) != null;   // a hit alone is not silence
        for (int i = 0; i < 4; i++) on |= SensaFrameEncoder.Quantize6(a[i]) > 0;
        return on;
    }

    /// <summary>The first hit of the frame as a transient record, or null.</summary>
    public static SensaTransient? Hit(in SideFeatures f, double gain)
    {
        if (f.TransientCount < 1) return null;
        double s = HapticLimiter.Apply(f.T0Amplitude, gain);
        return SensaFrameEncoder.Quantize6(s) > 0 ? new SensaTransient(s, f.T0Position) : null;
    }

    private static void Fold(double[] a, double pos, double amp)
    {
        int i = Math.Clamp((int)Math.Round(pos * 3), 0, 3);
        a[i] = Math.Max(a[i], amp);
    }
}
