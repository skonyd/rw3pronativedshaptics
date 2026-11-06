using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Fixed-capacity, source-clock-independent original 48k PCM transport.
/// Only four-channel UAC rear L/R bits enter the stereo queue. All metrics
/// refer to the RW3 software queue, NOT a game-to-motor latency measurement.
/// </summary>
public sealed class RealtimeStereoPcmTransport
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly long[] _arrivalTicks;
    private readonly int _stereoStride;
    private readonly int _sourceStride;
    private int _readFrame, _writeFrame, _bufferedFrames, _peakBufferedFrames;
    private long _offeredFrames, _deliveredFrames, _droppedFrames, _underrunFrames;
    private long _lastWriteTick;
    private double _lastTransitMs, _maxTransitMs, _sumTransitMs;
    private double _lastSourceGapMs, _maxSourceGapMs;

    public HapticPcmFormat Format { get; }
    public int CapacityFrames { get; }
    public const int SampleRate = HapticStereoWave.SampleRate;

    public RealtimeStereoPcmTransport(HapticPcmFormat format, int capacityMs = 120)
    {
        if (capacityMs is < 20 or > 500 || capacityMs % 10 != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMs),
                "Capacity must be 20–500ms in multiples of 10ms.");
        Format = format;
        _stereoStride = checked(2 * HapticStereoWave.BytesPerSample(format));
        _sourceStride = checked(_stereoStride * 2);
        CapacityFrames = checked(SampleRate * capacityMs / 1000);
        _ring = new byte[checked(CapacityFrames * _stereoStride)];
        _arrivalTicks = new long[CapacityFrames];
    }

    /// <summary>
    /// Copies all offered rear frames or accounts for exact whole-frame drops.
    /// An optional timestamp exists solely for deterministic offline tests.
    /// The timestamp is when RW3 received a callback, not game production time.
    /// </summary>
    public long WriteFourChannel(ReadOnlySpan<byte> fourChannel, long? callbackTick = null)
    {
        if (fourChannel.Length % _sourceStride != 0)
            throw new ArgumentException("Only whole four-channel input frames are valid.",
                nameof(fourChannel));
        int offered = fourChannel.Length / _sourceStride;
        if (offered == 0) return 0;

        long stamp = callbackTick ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastWriteTick != 0)
            {
                _lastSourceGapMs = TickAgeMs(_lastWriteTick, stamp);
                _maxSourceGapMs = Math.Max(_maxSourceGapMs, _lastSourceGapMs);
            }
            _lastWriteTick = stamp;
            _offeredFrames += offered;

            // Keep most recent complete frames on input larger than capacity;
            // no partial stereo samples and no fabricated frames.
            int skipped = Math.Max(0, offered - CapacityFrames);
            _droppedFrames += skipped;
            int retained = offered - skipped;
            int evicted = Math.Max(0, _bufferedFrames + retained - CapacityFrames);
            if (evicted > 0)
            {
                _readFrame = (_readFrame + evicted) % CapacityFrames;
                _bufferedFrames -= evicted;
                _droppedFrames += evicted;
            }

            int source = skipped * _sourceStride + _stereoStride;
            for (int i = 0; i < retained; i++)
            {
                fourChannel.Slice(source, _stereoStride)
                    .CopyTo(_ring.AsSpan(_writeFrame * _stereoStride, _stereoStride));
                _arrivalTicks[_writeFrame] = stamp;
                source += _sourceStride;
                if (++_writeFrame == CapacityFrames) _writeFrame = 0;
            }
            _bufferedFrames += retained;
            _peakBufferedFrames = Math.Max(_peakBufferedFrames, _bufferedFrames);
            return retained;
        }
    }

    /// <summary>
    /// Returns original stereo frames and zero-fills any requested shortfall.
    /// The discard-only diagnostics consumer requests ONLY available frames,
    /// so it does not invent underrun statistics during source silence.
    /// A future real hardware sink must request its true device-clock frames.
    /// </summary>
    public int ReadStereo(Span<byte> destination, long? consumeTick = null)
    {
        if (destination.Length % _stereoStride != 0)
            throw new ArgumentException("Only whole two-channel output frames are valid.",
                nameof(destination));
        int requested = destination.Length / _stereoStride;
        if (requested == 0) return 0;

        long stamp = consumeTick ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            int actual = Math.Min(requested, _bufferedFrames);
            int left = actual, destOffset = 0;
            while (left > 0)
            {
                int contiguous = Math.Min(left, CapacityFrames - _readFrame);
                _ring.AsSpan(_readFrame * _stereoStride, contiguous * _stereoStride)
                    .CopyTo(destination.Slice(destOffset, contiguous * _stereoStride));
                for (int i = 0; i < contiguous; ++i)
                {
                    double residence = TickAgeMs(_arrivalTicks[_readFrame + i], stamp);
                    _lastTransitMs = residence;
                    _maxTransitMs = Math.Max(_maxTransitMs, residence);
                    _sumTransitMs += residence;
                }
                destOffset += contiguous * _stereoStride;
                _readFrame = (_readFrame + contiguous) % CapacityFrames;
                left -= contiguous;
            }
            _bufferedFrames -= actual;
            _deliveredFrames += actual;
            int missing = requested - actual;
            if (missing > 0)
            {
                destination.Slice(destOffset).Clear();
                _underrunFrames += missing;
            }
            return actual;
        }
    }

    public StereoPcmTransportSnapshot Snapshot(long? sampleTick = null)
    {
        long stamp = sampleTick ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            double oldestAge = _bufferedFrames > 0
                ? TickAgeMs(_arrivalTicks[_readFrame], stamp) : 0;
            double sourceAge = _lastWriteTick != 0
                ? TickAgeMs(_lastWriteTick, stamp) : 0;
            return new(CapacityFrames, _bufferedFrames, _offeredFrames,
                _deliveredFrames, _droppedFrames, _underrunFrames,
                _peakBufferedFrames, oldestAge, _lastTransitMs, _maxTransitMs,
                _deliveredFrames == 0 ? 0 : _sumTransitMs / _deliveredFrames,
                _lastSourceGapMs, _maxSourceGapMs, sourceAge);
        }
    }

    private static double TickAgeMs(long start, long end) =>
        end <= start ? 0 : (end - start) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Explicit endpoint switch / user STOP discards all retained samples,
    /// historical measurements and arrival metadata.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            Array.Clear(_arrivalTicks);
            _readFrame = _writeFrame = _bufferedFrames = _peakBufferedFrames = 0;
            _offeredFrames = _deliveredFrames = _droppedFrames = _underrunFrames = 0;
            _lastWriteTick = 0;
            _lastTransitMs = _maxTransitMs = _sumTransitMs = 0;
            _lastSourceGapMs = _maxSourceGapMs = 0;
        }
    }
}

public readonly record struct StereoPcmTransportSnapshot(
    int CapacityFrames, int BufferedFrames, long OfferedFrames,
    long DeliveredFrames, long DroppedFrames, long UnderrunFrames,
    int PeakBufferedFrames, double OldestBufferedAgeMs,
    double LastTransitMs, double MaxTransitMs, double MeanTransitMs,
    double LastSourceGapMs, double MaxSourceGapMs, double SinceLastSourceMs)
{
    public double CapacityMs => CapacityFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double BufferedMs => BufferedFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double PeakBufferedMs => PeakBufferedFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double LostOverrunMs => DroppedFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
    public double UnderrunMs => UnderrunFrames * 1000.0 / RealtimeStereoPcmTransport.SampleRate;
}

/// <summary>
/// Diagnostics-only event-driven consumer. Unlike the old PeriodicTimer,
/// source frame arrival wakes the worker immediately, not at the next 20ms
/// poll. Each drain is <=240 real frames (5ms at 48k), with no invented
/// silence and no motor/vendor output. Task scheduling is NOT hard realtime.
/// </summary>
public sealed class StereoPcmDiscardProbe : IDisposable
{
    private readonly object _wakeGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _reader;
    private bool _wakeOutstanding, _stopping;
    private long _wakeCount, _drainedChunks;

    public RealtimeStereoPcmTransport Transport { get; }
    public const int ChunkMs = 5;
    public const int ChunkFrames = RealtimeStereoPcmTransport.SampleRate * ChunkMs / 1000;
    public long WakeCount => Interlocked.Read(ref _wakeCount);
    public long DrainedChunks => Interlocked.Read(ref _drainedChunks);

    public StereoPcmDiscardProbe(RealtimeStereoPcmTransport transport)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reader = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Safe, constant-time notification invoked after a real PCM write from
    /// the source callback. Multiple writes coalesce to one pending wake.
    /// </summary>
    public void NotifyDataArrived()
    {
        lock (_wakeGate)
        {
            if (_stopping || _wakeOutstanding) return;
            _wakeOutstanding = true;
            _wake.Release();
        }
    }

    private async Task DrainAsync()
    {
        int frameStride = 2 * HapticStereoWave.BytesPerSample(Transport.Format);
        byte[] scratch = new byte[ChunkFrames * frameStride];
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_cancel.Token).ConfigureAwait(false);
                lock (_wakeGate) _wakeOutstanding = false;
                Interlocked.Increment(ref _wakeCount);

                // Drain complete original frames already present: never read a
                // fixed 5ms block when fewer frames arrived, because a null
                // sink has no external clock against which to count underruns.
                while (true)
                {
                    int ready = Transport.Snapshot().BufferedFrames;
                    if (ready <= 0) break;
                    int take = Math.Min(ChunkFrames, ready);
                    Transport.ReadStereo(scratch.AsSpan(0, take * frameStride));
                    Interlocked.Increment(ref _drainedChunks);
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        finally { Array.Clear(scratch); }
    }

    public void Dispose()
    {
        lock (_wakeGate)
        {
            if (_stopping) return;
            _stopping = true;
            _cancel.Cancel();
        }
        try { _reader.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            Transport.Reset();
            _wake.Dispose();
            _cancel.Dispose();
        }
    }
}
