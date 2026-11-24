using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace RW3ProNativeDSHaptics;

/// <summary>Experiment (E-20261124-88): give the virtual DualSense's audio stream the start-up slack a real host
/// controller has. Windows' USB trace (E-20261124-87): on a real DualSense the first URB completes 13.3 ms after its
/// submission (ASAP scheduling a few frames ahead), two URBs stay in flight and each completion brings one new
/// submission, every 10 ms. On HIDMaestro the first completes exactly 10 ms after it (the stream cursor anchors at
/// "now"), and Windows settles into submitting two URBs after every other completion: the endpoint drains in 20 ms
/// bursts. This moves HIDMaestro's OUT stream cursor and the URBs already waiting <see cref="SlackMs"/> later, once
/// per stream start, so the stream runs with the same few milliseconds of slack. Armed only by
/// <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\iso-slack.flag</c>; reflection on HIDMaestro internals.</summary>
public sealed class IsoStartSlack
{
    public const double SlackMs = 3.5;
    public static string FlagFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "iso-slack.flag");

    private readonly object? _engine;
    private readonly FieldInfo? _lock, _pending, _outCursor;
    private long _lastBlock;
    public static readonly bool Armed = File.Exists(FlagFile);

    public IsoStartSlack(object? usbAudio)
    {
        if (!Armed || usbAudio == null) return;
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        _engine = usbAudio.GetType().GetField("_engine", F)?.GetValue(usbAudio);
        var t = _engine?.GetType();
        _lock = t?.GetField("_lock", F); _pending = t?.GetField("_pending", F); _outCursor = t?.GetField("_outCursor", F);
        DiagnosticsService.Info("ISO start slack experiment: " + (_lock != null && _pending != null && _outCursor != null ? $"armed, {SlackMs} ms" : "HIDMaestro fields not found, off"));
    }

    /// <summary>Called with every block of the OUT stream (on HIDMaestro's pump thread, after a completion). The first
    /// block after a pause of 100 ms or more is a stream start: shift the cursor and the waiting URBs once.</summary>
    public void OnBlock()
    {
        if (_engine == null || _lock == null || _pending == null || _outCursor == null) return;
        long now = Stopwatch.GetTimestamp(), last = _lastBlock;
        _lastBlock = now;
        if (last != 0 && now - last < Stopwatch.Frequency / 10) return;
        try
        {
            long shift = (long)(SlackMs * Stopwatch.Frequency / 1000);
            int moved = 0;
            var gate = _lock.GetValue(_engine)!;
            lock (gate)
            {
                _outCursor.SetValue(_engine, (long)_outCursor.GetValue(_engine)! + shift);
                if (_pending.GetValue(_engine) is IList list)
                    foreach (var urb in list)
                    {
                        var ut = urb.GetType();
                        if ((bool)ut.GetField("IsIn")!.GetValue(urb)!) continue;
                        var due = ut.GetField("DueTick")!;
                        due.SetValue(urb, (long)due.GetValue(urb)! + shift);
                        moved++;
                    }
            }
            DiagnosticsService.Info($"ISO start slack: stream start, cursor and {moved} waiting URB(s) moved {SlackMs} ms later.");
        }
        catch (Exception ex) { DiagnosticsService.Info("ISO start slack failed: " + ex.Message); }
    }
}
