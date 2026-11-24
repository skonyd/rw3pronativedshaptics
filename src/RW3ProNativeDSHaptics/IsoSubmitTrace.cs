using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>Diagnostics (E-20261123-86): when do Windows' isochronous URBs for the virtual DualSense's audio reach
/// HIDMaestro? HIDMaestro completes them every 10 ms (E-20261123-81), yet Windows drains the endpoint every other
/// period (E-20261123-83). This samples HIDMaestro's private pending-URB list about every 0.5 ms for 10 s and writes,
/// per URB: when it was first seen (its submission, to the sampling step), its due time, packet count and direction,
/// and when it left the list (its completion). Armed by <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\iso-trace.flag</c>,
/// which it deletes; writes <c>iso-trace.txt</c> next to it. Reads only; takes HIDMaestro's lock for a copy.</summary>
public sealed class IsoSubmitTrace
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics");
    public static string FlagFile => Path.Combine(Dir, "iso-trace.flag");
    public static string OutFile => Path.Combine(Dir, "iso-trace.txt");

    private readonly object? _usbAudio;
    private readonly Func<bool> _alive;

    private IsoSubmitTrace(object? usbAudio, Func<bool> alive) { _usbAudio = usbAudio; _alive = alive; }

    /// <summary>Starts a background watcher for the flag while <paramref name="alive"/> holds.</summary>
    public static void Watch(object? usbAudio, Func<bool> alive)
    {
        if (usbAudio == null) return;
        var t = new Thread(new IsoSubmitTrace(usbAudio, alive).Loop) { IsBackground = true, Name = "RW3 iso trace" };
        t.Start();
    }

    private void Loop()
    {
        while (_alive())
        {
            Thread.Sleep(1000);
            try
            {
                if (!File.Exists(FlagFile)) continue;
                File.Delete(FlagFile);
                DiagnosticsService.Info("ISO trace: " + Run(10));
            }
            catch (Exception ex) { DiagnosticsService.Info("ISO trace failed: " + ex.Message); }
        }
    }

    private string Run(int seconds)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        var engine = _usbAudio!.GetType().GetField("_engine", F)?.GetValue(_usbAudio);
        if (engine == null) return "no engine";
        var et = engine.GetType();
        var gate = et.GetField("_lock", F)?.GetValue(engine);
        if (et.GetField("_pending", F)?.GetValue(engine) is not IList pending || gate == null) return "no pending list";
        Type? urbType = null;
        FieldInfo? fSeq = null, fDue = null, fCount = null, fIn = null;
        var seen = new Dictionary<uint, (double seenMs, double dueMs, int packets, bool isIn)>();
        var rows = new List<string> { "seq seen_ms due_ms packets dir gone_ms" };
        long start = Stopwatch.GetTimestamp(), end = start + seconds * Stopwatch.Frequency;
        double Ms(long t) => (t - start) * 1000.0 / Stopwatch.Frequency;
        var snapshot = new List<object>(16);
        var present = new HashSet<uint>();
        int samples = 0;
        while (Stopwatch.GetTimestamp() < end)
        {
            snapshot.Clear();
            lock (gate) foreach (var o in pending) snapshot.Add(o);
            long now = Stopwatch.GetTimestamp();
            samples++;
            present.Clear();
            foreach (var o in snapshot)
            {
                if (urbType == null)
                {
                    urbType = o.GetType();
                    fSeq = urbType.GetField("Seqnum"); fDue = urbType.GetField("DueTick"); fCount = urbType.GetField("PacketCount"); fIn = urbType.GetField("IsIn");
                    if (fSeq == null || fDue == null || fCount == null || fIn == null) return "unexpected URB fields";
                }
                uint seq = (uint)fSeq!.GetValue(o)!;
                present.Add(seq);
                if (!seen.ContainsKey(seq)) seen[seq] = (Ms(now), Ms((long)fDue!.GetValue(o)!), (int)fCount!.GetValue(o)!, (bool)fIn!.GetValue(o)!);
            }
            foreach (var kv in new List<KeyValuePair<uint, (double, double, int, bool)>>(seen))
                if (!present.Contains(kv.Key))
                {
                    var (s, d, p, i) = kv.Value;
                    rows.Add(FormattableString.Invariant($"{kv.Key} {s:F2} {d:F2} {p} {(i ? "in" : "out")} {Ms(now):F2}"));
                    seen.Remove(kv.Key);
                }
            // about 0.5 ms per sample without a 15.6 ms sleep
            long next = now + Stopwatch.Frequency / 2000;
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
        }
        foreach (var kv in seen) { var (s, d, p, i) = kv.Value; rows.Add(FormattableString.Invariant($"{kv.Key} {s:F2} {d:F2} {p} {(i ? "in" : "out")} -")); }
        File.WriteAllLines(OutFile, rows);
        return $"{rows.Count - 1} URBs, {samples} samples, written to {OutFile}";
    }
}
