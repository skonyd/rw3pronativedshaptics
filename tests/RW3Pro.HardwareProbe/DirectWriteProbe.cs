using System.IO;
using System.Text.Json;
using RW3ProNativeDSHaptics;

/// <summary>
/// Consented experiment (user, 2026-11-08): write captured Synapse frames straight to the Wolverine
/// with SensaHidTransport and listen with the webcam mic. Only frames seen in the USB capture are sent.
/// Each candidate interface (cable / dongle) is tried separately: left, right, then silence tail.
/// </summary>
internal static class DirectWriteProbe
{
    // Captured 2026-11-08 (TestResults/usb-capture-20261108): 60 Hz test pulse, amplitude 1.0.
    private static readonly byte[] Left = Convert.FromHexString("79fc57e2bf15f8a800000000000040000000000001003001800c006800000000000040000000000000");
    private static readonly byte[] Silence = Convert.FromHexString("79003001800c006800000000000040000000000001003001800c006800000000000040000000000000");
    private static readonly byte[] Right = Convert.FromHexString("79003001800c006800000000000040000000000001fc57e2bf15f8a800000000000040000000000000");

    public static async Task RunAsync(string micId, string reportPath)
    {
        var results = new List<object>();
        var candidates = SensaHidTransport.Candidates();
        Console.WriteLine($"candidates: {candidates.Count}");
        foreach (var c in candidates) Console.WriteLine("  " + c);
        using var mic = new MicrophoneLevelProbe(micId, collectToneBands: true);
        await Task.Delay(1000);
        foreach (string path in candidates)
        {
            using var tx = new SensaHidTransport();
            bool opened = tx.Open(path) && tx.DevicePath == path;
            Console.WriteLine($"open {(opened ? "ok" : "FAILED err " + tx.LastError)}: {path}");
            if (!opened) { results.Add(new { Path = path, Opened = false, tx.LastError }); continue; }
            async Task<object> Drive(string name, byte[]? frame, int ms)
            {
                mic.BeginWindow(); await Task.Delay(400); var bg = mic.ReadWindow();
                mic.BeginWindow();
                var sw = System.Diagnostics.Stopwatch.StartNew(); int sent = 0, failed = 0; long next = 0;
                while (sw.ElapsedMilliseconds < ms)
                {
                    if (frame != null && sw.ElapsedMilliseconds >= next) { if (tx.Send(frame)) sent++; else failed++; next += 33; }
                    await Task.Delay(2);
                }
                var driven = mic.ReadWindow();
                // Explicit silence: the controller keeps playing the last frame otherwise (run 1: +28 dB tail).
                for (int k = 0; k < 3; k++) { tx.Send(Silence); await Task.Delay(33); }
                mic.BeginWindow(); await Task.Delay(600); var after = mic.ReadWindow();
                double Db(double a, double b) => 20 * Math.Log10(Math.Max(a, 1e-12) / Math.Max(b, 1e-12));
                Console.WriteLine($"  {name,-6} sent {sent} failed {failed} (err {tx.LastError}) driven {Db(driven.BandRms, bg.BandRms):+0.0;-0.0} dB, after-stop {Db(after.BandRms, bg.BandRms):+0.0;-0.0} dB");
                return new { Step = name, Sent = sent, Failed = failed, tx.LastError, Background = bg, Driven = driven, After = after };
            }
            var steps = new List<object>
            {
                await Drive("none", null, 1500),
                await Drive("left", Left, 1500),
                await Drive("right", Right, 1500),
                await Drive("left", Left, 1500),
                await Drive("silence", Silence, 1500),
                await Drive("right", Right, 1500),
            };
            results.Add(new { Path = path, Opened = true, Steps = steps });
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Candidates = candidates, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("report: " + reportPath);
    }
}
