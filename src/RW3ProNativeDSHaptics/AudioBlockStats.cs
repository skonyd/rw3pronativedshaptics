using System;
using System.Diagnostics;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// How the virtual DualSense's audio stream reaches RW3: frames per second (48000 when the game's clock and the virtual
/// endpoint agree), the longest gap between two blocks, and the longest time RW3's handler kept a block. The handler runs
/// on HIDMaestro's audio thread; if it is slow or the stream stalls, the game's audio engine (which also feeds the TV)
/// can stall with it: 2026-11-23, The Last of Us Part II sounded quiet, lost sounds and repeated thumps.
/// </summary>
public sealed class AudioBlockStats
{
    private readonly object _gate = new();
    private long _frames, _windowStart, _lastBlock;
    private double _gapMaxMs, _handlerMaxMs;
    private int _blocks;

    /// <summary>One block of <paramref name="frames"/> frames whose handler started at Stopwatch <paramref name="start"/>.</summary>
    public void Note(int frames, long start, long? end = null)
    {
        long now = end ?? Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_windowStart == 0) _windowStart = start;
            if (_lastBlock != 0) _gapMaxMs = Math.Max(_gapMaxMs, (start - _lastBlock) * 1000.0 / Stopwatch.Frequency);
            _lastBlock = start;
            _handlerMaxMs = Math.Max(_handlerMaxMs, (now - start) * 1000.0 / Stopwatch.Frequency);
            _frames += frames; _blocks++;
        }
    }

    /// <summary>"; audio in 47998 fps, 100 blocks/s, gap max 12.0 ms, handler max 0.3 ms" for the window, then reset.</summary>
    public string Take(long? now = null)
    {
        lock (_gate)
        {
            if (_blocks == 0) return "";
            double seconds = ((now ?? Stopwatch.GetTimestamp()) - _windowStart) / (double)Stopwatch.Frequency;
            string s = seconds <= 0 ? "" : FormattableString.Invariant(
                $"; audio in {_frames / seconds:F0} fps, {_blocks / seconds:F0} blocks/s, gap max {_gapMaxMs:F1} ms, handler max {_handlerMaxMs:F2} ms");
            _frames = 0; _blocks = 0; _gapMaxMs = _handlerMaxMs = 0; _windowStart = 0;
            return s;
        }
    }
}
