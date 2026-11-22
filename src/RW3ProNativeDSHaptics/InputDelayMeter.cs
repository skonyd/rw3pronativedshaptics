using System;
using System.Diagnostics;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// How long the pad's input waits inside RW3: from the moment the GIP report arrived (WinUsbGipTransport) to the moment
/// the bridge handed it to the virtual pad. Counted once per new report; the bridge's periodic log line shows average and
/// worst case. It does not include the USB poll before RW3 (the pad reports every 4 ms) or the virtual pad's own report
/// stream after it (HIDMaestro, 4 ms for the DualSense profile).
/// </summary>
public sealed class InputDelayMeter
{
    private long _lastStamp;
    private double _sumMs, _maxMs;
    private int _count;

    /// <summary>The bridge submitted the report with this Stopwatch timestamp (repeats of the same report are ignored).</summary>
    public void Note(long inputStamp, long? now = null)
    {
        if (inputStamp == 0 || inputStamp == _lastStamp) return;
        _lastStamp = inputStamp;
        double ms = ((now ?? Stopwatch.GetTimestamp()) - inputStamp) * 1000.0 / Stopwatch.Frequency;
        if (ms < 0 || ms > 1000) return;   // clock oddity or a report from long ago
        _sumMs += ms; _count++;
        if (ms > _maxMs) _maxMs = ms;
    }

    /// <summary>"; input delay avg 0.4 max 1.2 ms (812 reports)" for the window, then reset; empty when nothing came.</summary>
    public string Take()
    {
        if (_count == 0) return "";
        string s = FormattableString.Invariant($"; input delay avg {_sumMs / _count:F1} max {_maxMs:F1} ms ({_count} reports)");
        _sumMs = _maxMs = 0; _count = 0;
        return s;
    }
}
