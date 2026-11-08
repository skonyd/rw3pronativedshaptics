using System.IO;
using System.Text.Json;
using RW3ProNativeDSHaptics;

/// <summary>
/// Frame-format study through the running engine (stable, unlike a bare RenderHapticsDebug loop):
/// CaptureFrames + CommandProbe record, every 33 ms, the exact values sent per hand and the frame
/// the provider rendered. Inputs: independent L/R amplitude sweep (manual pulses), pitch sweep (tones),
/// amplitude-modulated tones (distinct 4 points), click train (transients).
/// </summary>
internal static class FrameLearnProbe
{
    public static async Task RunAsync(Action<string> resolve, string sdk, string outPath)
    {
        resolve(sdk);
        bool multi = Environment.GetEnvironmentVariable("RW3_LEARN") == "multi";
        using var engine = new SensaStereoPrototype { CaptureFrames = true, SourceGainPercent = 100, OutputLevelPercent = 100,
            OutputMethod = multi ? SensaOutputMethod.Multiband : SensaOutputMethod.Standard };
        var lines = new List<string>();
        string phase = "start";
        engine.CommandProbe = (l, r, frame) =>
        {
            lock (lines) lines.Add(JsonSerializer.Serialize(new { phase, l, r, frame = Convert.ToHexString(frame).ToLowerInvariant() }));
        };
        if (!engine.Start()) throw new InvalidOperationException(engine.Status);
        for (int i = 0; i < 60 && !engine.IsActive; i++) await Task.Delay(50);
        if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
        await Task.Delay(500);

        double S(double hz, double t) => Math.Sin(2 * Math.PI * hz * t);
        if (multi)
        {
            async Task FeedM(Func<double, (double, double)> wave, double seconds)
            {
                var buf = new float[480]; long n = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                while (n < seconds * 48000)
                {
                    for (int i = 0; i < 240; i++) { var (a, b) = wave((n + i) / 48000.0); buf[2 * i] = (float)a; buf[2 * i + 1] = (float)b; }
                    engine.Features.PushStereo(buf); n += 240;
                    while (sw.Elapsed.TotalMilliseconds < n / 48.0) await Task.Delay(1);
                }
            }
            phase = "m-80"; await FeedM(t => (0.5 * S(80, t), 0), 2.5);
            phase = "m-80+190"; await FeedM(t => (0.25 * (S(80, t) + S(190, t)), 0), 3);
            phase = "m-40+120+300"; await FeedM(t => (0.2 * (S(40, t) + S(120, t) + S(300, t)), 0), 3);
            phase = "m-80x.35+190x.15"; await FeedM(t => (0.35 * S(80, t) + 0.15 * S(190, t), 0), 3);
            phase = "m-R190"; await FeedM(t => (0, 0.5 * S(190, t)), 2.5);
            phase = "m-L80+190-R120+300"; await FeedM(t => (0.25 * (S(80, t) + S(190, t)), 0.25 * (S(120, t) + S(300, t))), 3);
            phase = "silence"; await Task.Delay(600);
            Console.WriteLine("engine status before stop: " + engine.Status);
            engine.Stop();
            lock (lines) File.WriteAllLines(outPath, lines);
            Console.WriteLine($"LEARN {lines.Count} frames → {outPath}");
            return;
        }
        async Task Manual(float l, float r, int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { engine.Submit(l, r); await Task.Delay(5); } }
        phase = "amp-sweep";
        for (int i = 0; i <= 40; i++) await Manual(i / 40f, (40 - i) / 40f, 400);
        engine.Submit(0, 0); await Task.Delay(400);

        async Task Feed(Func<double, (double, double)> wave, double seconds)
        {
            var buf = new float[480]; long n = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
            while (n < seconds * 48000)
            {
                for (int i = 0; i < 240; i++) { var (a, b) = wave((n + i) / 48000.0); buf[2 * i] = (float)a; buf[2 * i + 1] = (float)b; }
                engine.Features.PushStereo(buf); n += 240;
                while (sw.Elapsed.TotalMilliseconds < n / 48.0) await Task.Delay(1);
            }
        }
        phase = "pitch-sweep";
        for (int hz = 30; hz <= 400; hz += 10) { int f = hz; await Feed(t => (0.5 * Math.Sin(2 * Math.PI * f * t), 0.3 * Math.Sin(2 * Math.PI * (430 - f) * t)), 0.4); }
        phase = "am";
        await Feed(t => (0.5 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 7 * t)) * Math.Sin(2 * Math.PI * 120 * t), 0.4 * Math.Abs(Math.Sin(2 * Math.PI * 3 * t)) * Math.Sin(2 * Math.PI * 200 * t)), 4.0);
        phase = "clicks";
        await Feed(t => { double l = t % 0.13; double v = l < 0.006 ? 0.9 * Math.Exp(-l / 0.002) * Math.Sin(2 * Math.PI * 150 * l) : 0; return (v, (t + 0.06) % 0.17 < 0.006 ? v : 0); }, 4.0);
        phase = "silence";
        await Task.Delay(600);
        Console.WriteLine("engine status before stop: " + engine.Status + " | active " + engine.IsActive + " | running " + engine.IsRunning);
        engine.Stop();
        lock (lines) File.WriteAllLines(outPath, lines);
        Console.WriteLine($"LEARN {lines.Count} frames → {outPath}");
    }
}
