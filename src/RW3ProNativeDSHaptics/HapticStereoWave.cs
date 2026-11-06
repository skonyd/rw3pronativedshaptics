using System;
using System.Buffers.Binary;
using System.IO;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Repackages 4ch 48kHz DualSense UAC into two-channel WAV for OFFLINE analysis
/// and properly licensed Wwise Motion Sensa import.
/// Only channels 2/3 (zero based) are copied; their PCM bits are NOT scaled,
/// filtered, remixed, resampled, clipped or generated. No live Sensa playback.
/// A file's audio does not prove game-authored provenance or motor fidelity.
/// </summary>
public enum HapticPcmFormat { Signed16, Float32 }

public static class HapticStereoWave
{
    public const int SampleRate = 48000;
    public const int PcmWaveHeaderLength = 44;
    public const int FloatWaveHeaderLength = 56;
    public const int MaximumSeconds = 10;

    public static int BytesPerSample(HapticPcmFormat format) => format switch
    {
        HapticPcmFormat.Signed16 => 2,
        HapticPcmFormat.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static byte[] ExtractRear(ReadOnlySpan<byte> fourChannel, HapticPcmFormat format)
    {
        int bytes = BytesPerSample(format);
        int inputStride = checked(bytes * 4);
        if (fourChannel.Length % inputStride != 0)
            throw new ArgumentException("Interleaved input must be whole 4-channel PCM frames.",
                nameof(fourChannel));
        byte[] output = new byte[fourChannel.Length / 2];
        for (int source = 0, dest = 0; source < fourChannel.Length; source += inputStride, dest += bytes * 2)
            fourChannel.Slice(source + bytes * 2, bytes * 2)
                .CopyTo(output.AsSpan(dest, bytes * 2));
        return output;
    }

    public static byte[] BuildWav(ReadOnlySpan<byte> rearStereo, HapticPcmFormat format)
    {
        int bytes = BytesPerSample(format);
        int stride = bytes * 2;
        if (rearStereo.IsEmpty || rearStereo.Length % stride != 0)
            throw new ArgumentException("Stereo PCM must contain whole nonempty frames.", nameof(rearStereo));
        if (rearStereo.Length > MaximumSeconds * SampleRate * stride)
            throw new ArgumentOutOfRangeException(nameof(rearStereo), "WAV export limited to 10s.");
        bool floating = format == HapticPcmFormat.Float32;
        int headerLength = floating ? FloatWaveHeaderLength : PcmWaveHeaderLength;
        byte[] wav = new byte[checked(headerLength + rearStereo.Length)];
        var header = wav.AsSpan(0, headerLength);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)(wav.Length - 8));
        "WAVE"u8.CopyTo(header.Slice(8));
        "fmt "u8.CopyTo(header.Slice(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20),
            format == HapticPcmFormat.Float32 ? (ushort)3 : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28), (uint)(SampleRate * stride));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32), (ushort)stride);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34), (ushort)(bytes * 8));
        int dataOffset = 36;
        if (floating)
        {
            // IEEE_FLOAT WAVE requires the standard fact sample-count chunk.
            "fact"u8.CopyTo(header.Slice(36));
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(44),
                (uint)(rearStereo.Length / stride));
            dataOffset = 48;
        }
        "data"u8.CopyTo(header.Slice(dataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(dataOffset + 4), (uint)rearStereo.Length);
        rearStereo.CopyTo(wav.AsSpan(headerLength));
        return wav;
    }

    public static byte[] FromFourChannel(ReadOnlySpan<byte> fourChannel, HapticPcmFormat format) =>
        BuildWav(ExtractRear(fourChannel, format), format);
}

/// <summary>
/// Only active after explicit consent; stores a MAXIMUM of ten seconds in
/// private RAM. No disk writes occur in a WASAPI callback, no source changes,
/// and no automatic uploads. Call End() to produce an offline stereo WAV.
/// </summary>
public sealed class HapticStereoRecorder
{
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private readonly HapticPcmFormat _format;
    private int _written;
    private bool _ended;

    public HapticStereoRecorder(HapticPcmFormat format, int seconds = 10)
    {
        if (seconds is < 1 or > HapticStereoWave.MaximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _format = format;
        _buffer = new byte[checked(seconds * HapticStereoWave.SampleRate *
            2 * HapticStereoWave.BytesPerSample(format))];
    }

    public int CapturedFrames
    {
        get { lock (_gate) return _written / (2 * HapticStereoWave.BytesPerSample(_format)); }
    }

    public bool IsFull
    {
        get { lock (_gate) return _written == _buffer.Length; }
    }

    public bool HasSamples => CapturedFrames > 0;

    public void Accept(ReadOnlySpan<byte> fourChannelFrames)
    {
        int bytes = HapticStereoWave.BytesPerSample(_format);
        int stride = bytes * 4, rearStride = bytes * 2;
        if (fourChannelFrames.Length % stride != 0)
            throw new ArgumentException("Only complete four-channel source frames are allowed.");
        lock (_gate)
        {
            if (_ended || _written == _buffer.Length) return;
            int acceptedFrames = Math.Min(fourChannelFrames.Length / stride,
                (_buffer.Length - _written) / rearStride);
            for (int i = 0; i < acceptedFrames; i++)
            {
                fourChannelFrames.Slice(i * stride + bytes * 2, rearStride)
                    .CopyTo(_buffer.AsSpan(_written + i * rearStride, rearStride));
            }
            _written += acceptedFrames * rearStride;
        }
    }

    public byte[] End()
    {
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("Recorder already finalized.");
            _ended = true;
            if (_written == 0)
                throw new InvalidOperationException("No USB haptic PCM frames were received.");
            return HapticStereoWave.BuildWav(_buffer.AsSpan(0, _written), _format);
        }
    }

    public void Abandon()
    {
        lock (_gate)
        {
            _ended = true;
            // Minimize retention of recorded game audio when discarded.
            Array.Clear(_buffer);
            _written = 0;
        }
    }
}
