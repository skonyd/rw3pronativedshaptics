using System;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Fixed-capacity, thread-safe native stereo PCM byte transport at 48kHz.
/// Producer accepts a 4ch source and copies ONLY rear L/R (channels 3/4)
/// sample bits into a 2ch ring. No DSP, RMS conversion, gain, resampling,
/// audio device ownership, vendor calls or disk I/O.
/// 
/// This is a SOURCE/TRANSPORT contract, not a proven Sensa sink. On overflow
/// the oldest complete stereo frames are discarded explicitly; on underrun
/// missing output frames are zero filled, never repeated from stale data.
/// </summary>
public sealed class RealtimeStereoPcmTransport
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly int _stereoStride;
    private readonly int _sourceStride;
    private int _readFrame, _writeFrame, _bufferedFrames;
    private long _offeredFrames, _deliveredFrames, _droppedFrames, _underrunFrames;

    public HapticPcmFormat Format { get; }
    public int CapacityFrames { get; }
    public const int SampleRate = HapticStereoWave.SampleRate;

    public RealtimeStereoPcmTransport(HapticPcmFormat format, int capacityMs = 120)
    {
        if (capacityMs < 20 || capacityMs > 500 || capacityMs % 10 != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMs),
                "Capacity must be 20–500ms in multiples of 10ms.");
        Format = format;
        _stereoStride = checked(2 * HapticStereoWave.BytesPerSample(format));
        _sourceStride = _stereoStride * 2;
        CapacityFrames = checked(SampleRate * capacityMs / 1000);
        _ring = new byte[checked(CapacityFrames * _stereoStride)];
    }

    /// <summary>
    /// True number of input frames presented, including frames lost to overrun.
    /// </summary>
    public long WriteFourChannel(ReadOnlySpan<byte> fourChannel)
    {
        if (fourChannel.Length % _sourceStride != 0)
            throw new ArgumentException("Only whole four-channel input frames are valid.",
                nameof(fourChannel));

        int offered = fourChannel.Length / _sourceStride;
        if (offered == 0) return 0;
        lock (_gate)
        {
            _offeredFrames += offered;

            // If a callback arrives larger than our capacity, retain only
            // the newest complete frames. The skipped prefix is counted.
            int skip = Math.Max(0, offered - CapacityFrames);
            _droppedFrames += skip;
            int retained = offered - skip;

            int evict = Math.Max(0, _bufferedFrames + retained - CapacityFrames);
            if (evict > 0)
            {
                _readFrame = (_readFrame + evict) % CapacityFrames;
                _bufferedFrames -= evict;
                _droppedFrames += evict;
            }

            int src = skip * _sourceStride + _stereoStride;
            for (int i = 0; i < retained; ++i)
            {
                fourChannel.Slice(src, _stereoStride)
                    .CopyTo(_ring.AsSpan(_writeFrame * _stereoStride, _stereoStride));
                src += _sourceStride;
                _writeFrame++;
                if (_writeFrame == CapacityFrames) _writeFrame = 0;
            }
            _bufferedFrames += retained;
            return retained;
        }
    }

    /// <summary>
    /// Reads available original stereo frames in chronological order. Any
    /// requested frames not yet received are explicitly written as silence.
    /// Returns genuine frames (excluding synthesized zero padding).
    /// </summary>
    public int ReadStereo(Span<byte> destination)
    {
        if (destination.Length % _stereoStride != 0)
            throw new ArgumentException("Only whole two-channel output frames are valid.",
                nameof(destination));
        int request = destination.Length / _stereoStride;
        if (request == 0) return 0;
        lock (_gate)
        {
            int available = Math.Min(request, _bufferedFrames);
            int remaining = available;
            int offset = 0;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, CapacityFrames - _readFrame);
                _ring.AsSpan(_readFrame * _stereoStride, chunk * _stereoStride)
                    .CopyTo(destination.Slice(offset, chunk * _stereoStride));
                offset += chunk * _stereoStride;
                _readFrame = (_readFrame + chunk) % CapacityFrames;
                remaining -= chunk;
            }
            _bufferedFrames -= available;
            _deliveredFrames += available;

            int missing = request - available;
            if (missing > 0)
            {
                destination.Slice(offset).Clear();
                _underrunFrames += missing;
            }
            return available;
        }
    }

    public StereoPcmTransportSnapshot Snapshot()
    {
        lock (_gate)
            return new(CapacityFrames, _bufferedFrames, _offeredFrames,
                _deliveredFrames, _droppedFrames, _underrunFrames);
    }

    /// <summary>
    /// Detaches all retained PCM on endpoint switch or explicit user stop.
    /// No data survives the transport's reset.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _readFrame = _writeFrame = _bufferedFrames = 0;
            _offeredFrames = _deliveredFrames = _droppedFrames = _underrunFrames = 0;
        }
    }
}

public readonly record struct StereoPcmTransportSnapshot(
    int CapacityFrames, int BufferedFrames, long OfferedFrames,
    long DeliveredFrames, long DroppedFrames, long UnderrunFrames)
{
    public double CapacityMs => CapacityFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double BufferedMs => BufferedFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double LostOverrunMs => DroppedFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double UnderrunMs => UnderrunFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
}

/// <summary>
/// A deliberately DISCARDING (no haptic device) consumer to validate full
/// PCM block transport and queue health with a real 4ch source. Its 20ms
/// tick consumes 960 original 48kHz frames, NOT a lossy 50Hz RMS envelope.
/// OS task scheduling is approximate; metrics are software-only.
/// </summary>
public sealed class StereoPcmDiscardProbe : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _reader;
    public RealtimeStereoPcmTransport Transport { get; }
    public const int ChunkMs = 20;

    public StereoPcmDiscardProbe(RealtimeStereoPcmTransport transport)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reader = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        var scratch = new byte[
            RealtimeStereoPcmTransport.SampleRate * ChunkMs / 1000 *
            2 * HapticStereoWave.BytesPerSample(Transport.Format)];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ChunkMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_cancel.Token))
                Transport.ReadStereo(scratch);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        finally { Array.Clear(scratch); }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        try { _reader.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            Transport.Reset();
            _cancel.Dispose();
        }
    }
}
