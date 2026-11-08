using System;
using System.Buffers;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

/// <summary>Serial, bounded sink delivery. A clock argument makes silence/stop safety independently testable.</summary>
public sealed class AudioSinkDelivery : IDisposable
{
    public const int SilenceTimeoutMs = 250;
    public const int MaximumQueuedSamples = 48000 * 2 * 2; // At most 2s of stereo; intentional delay <=1s.
    private readonly object _gate = new();
    private readonly IAudioHapticSink _sink;
    private readonly Queue<(long Due, float[] Data, int Length)> _queue = new();
    private int _queuedSamples;
    private bool _stopped, _silent = true;
    private long _lastDeliveredAt;
    public long DroppedSamples { get; private set; }
    public int QueuedSamples { get { lock (_gate) return _queuedSamples; } }

    public AudioSinkDelivery(IAudioHapticSink sink) => _sink = sink;

    public void Configure(Action<IAudioHapticSink> update)
    {
        lock (_gate)
        {
            if (_stopped) return;
            update(_sink);
            // Serialize with Accept and zero the previous command immediately.
            _sink.Silence();
            _silent = true;
        }
    }

    public void Enqueue(ReadOnlySpan<float> stereo, long nowMs, int delayMs)
    {
        if (stereo.Length % 2 != 0 || stereo.Length > MaximumQueuedSamples)
            throw new ArgumentException("Bounded complete stereo block required.", nameof(stereo));
        lock (_gate)
        {
            if (_stopped || stereo.IsEmpty) return;
            if (_queuedSamples + stereo.Length > MaximumQueuedSamples)
            {
                DroppedSamples += _queuedSamples + stereo.Length;
                StopLocked();
                throw new InvalidOperationException("Audio haptic queue exceeded its 2s bound; output stopped.");
            }
            var data = ArrayPool<float>.Shared.Rent(stereo.Length);
            stereo.CopyTo(data);
            _queue.Enqueue((checked(nowMs + Math.Clamp(delayMs, 0, 1000)), data, stereo.Length));
            _queuedSamples += stereo.Length;
        }
    }

    public void Pump(long nowMs)
    {
        lock (_gate)
        {
            if (_stopped) return;
            try
            {
                while (_queue.Count > 0 && _queue.Peek().Due <= nowMs)
                {
                    var item = _queue.Dequeue();
                    _queuedSamples -= item.Length;
                    try { _sink.Accept(item.Data.AsSpan(0, item.Length)); }
                    finally { ArrayPool<float>.Shared.Return(item.Data, clearArray: true); }
                    _lastDeliveredAt = nowMs;
                    _silent = false;
                }
                // Never cut an intentionally delayed tail. Once drained, missing callbacks cannot latch motors.
                if (_queue.Count == 0 && !_silent && nowMs - _lastDeliveredAt >= SilenceTimeoutMs)
                {
                    _sink.Silence();
                    _silent = true;
                }
            }
            catch { StopLocked(); throw; }
        }
    }

    private void StopLocked()
    {
        _stopped = true;
        while (_queue.Count > 0) ArrayPool<float>.Shared.Return(_queue.Dequeue().Data, clearArray: true);
        _queuedSamples = 0;
        _sink.Silence();
        _silent = true;
    }
    public void Dispose() { lock (_gate) { if (!_stopped) StopLocked(); } }
}
