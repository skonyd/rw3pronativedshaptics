using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Frame-format study: drives the licensed HAR/provider with known parametric inputs and records the
/// 41/44-byte frames the provider renders (RenderHapticsDebug), without a USB capture. Each case is a
/// left-hand effect with a constant or 4-point amplitude curve, a constant pitch and optional
/// transients; the right hand stays idle. Output: JSON lines {amp[4], pitch, transients, frame hex}.
/// </summary>
internal static class FrameSweepProbe
{
    [DllImport("HAR")] static extern bool Init();
    [DllImport("HAR")] static extern void Quit();
    [DllImport("HAR")] static extern int AddParametricEffect(double[] a, int an, double[] p, int pn, double fmin, double fmax, double[]? t, int tn, bool loop);
    [DllImport("HAR")] static extern bool UpdateParametricEffect(int id, double[] a, int an, double[] p, int pn, double fmin, double fmax, double[]? t, int tn, bool loop);
    [DllImport("HAR")] static extern void AddTargetToEventMarshal(int id, Cmd[] t, int n);
    [DllImport("HAR")] static extern void SetEventIntensity(int id, double v);
    [DllImport("HAR")] static extern void PlayEvent(int id, double offset, double t, double s);
    [DllImport("HAR")] static extern void ComputeAllEvents(double t);
    [DllImport("HAR")] static extern void StopAllEvents();
    [DllImport("Interhaptics.RazerProvider")] static extern bool ProviderInit();
    [DllImport("Interhaptics.RazerProvider")] static extern bool ProviderClean();
    [DllImport("Interhaptics.RazerProvider")] static extern int RenderHapticsDebug([Out] byte[] buffer, int index, int size);
    [StructLayout(LayoutKind.Sequential)] struct Cmd { public int Group, Side; public Cmd(int g, int s) { Group = g; Side = s; } }

    public static void Run(string sdk, string outPath)
    {
        NativeLibrary.SetDllImportResolver(typeof(FrameSweepProbe).Assembly, (name, _, _) =>
            name is "HAR" or "Interhaptics.RazerProvider" ? NativeLibrary.Load(Path.Combine(sdk, name + ".dll")) : IntPtr.Zero);
        if (!Init()) throw new InvalidOperationException("HAR.Init failed");
        double[] silent = { 0, 0, 1, 0 };
        int left = AddParametricEffect(silent, 4, null!, 0, 30, 400, null, 0, false);
        AddTargetToEventMarshal(left, new[] { new Cmd(302, 2) }, 1);   // Sage T1 left palm (same target as the engine)
        SetEventIntensity(left, 1);
        if (!ProviderInit()) throw new InvalidOperationException("provider init failed (Synapse/Sensa not attached?)");
        var buf = new byte[256];
        using var w = new StreamWriter(outPath);
        double clock = 1.0;
        int n = 0;
        void Case(string kind, double[] amp4, double pitch, double[]? tr)
        {
            const double T = 0.033, hold = 0.030;
            double[] a = { 0, amp4[0], T / 3, amp4[1], 2 * T / 3, amp4[2], T, amp4[3], T + hold, amp4[3] };
            double[] p = { 0, pitch, T + hold, pitch };
            UpdateParametricEffect(left, a, a.Length, p, p.Length, 30, 400, tr, tr?.Length ?? 0, false);
            PlayEvent(left, -clock, 0, 0);
            ComputeAllEvents(clock);
            int len = RenderHapticsDebug(buf, 0, buf.Length);
            string hex = len > 0 && len <= buf.Length ? Convert.ToHexString(buf, 0, len).ToLowerInvariant() : "";
            w.WriteLine(JsonSerializer.Serialize(new { kind, amp = amp4, pitch, tr, len, frame = hex }));
            // no StopAllEvents here: RenderHapticsDebug throws (SEH) when no event has a material frame
            clock += 0.2; n++;
        }
        // 1) constant amplitude × pitch grid
        foreach (double pitch in new[] { 0.0, 0.05, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 })
            for (int i = 0; i <= 20; i++) { double v = i / 20.0; Case("const", new[] { v, v, v, v }, pitch, null); }
        // 2) fine amplitude steps at one pitch
        for (int i = 0; i <= 255; i++) { double v = i / 255.0; Case("fine", new[] { v, v, v, v }, 0.08, null); }
        // 3) each of the 4 points alone
        for (int k = 0; k < 4; k++) foreach (double v in new[] { 0.25, 0.5, 1.0 })
            { var a = new double[4]; a[k] = v; Case("point" + k, a, 0.08, null); }
        // 4) ramps
        Case("ramp-up", new[] { 0.0, 0.33, 0.66, 1.0 }, 0.08, null);
        Case("ramp-down", new[] { 1.0, 0.66, 0.33, 0.0 }, 0.08, null);
        // 5) fine pitch steps at amplitude 1
        for (int i = 0; i <= 100; i++) Case("pitch", new[] { 1.0, 1.0, 1.0, 1.0 }, i / 100.0, null);
        // 6) transients: position × amplitude (one), then two
        foreach (double pos in new[] { 0.0, 0.25, 0.5, 0.75, 0.99 })
            foreach (double ta in new[] { 0.25, 0.5, 1.0 })
                Case("tr1", new[] { 0.5, 0.5, 0.5, 0.5 }, 0.2, new[] { pos * 0.033, ta, 0.2 });
        Case("tr2", new[] { 0.5, 0.5, 0.5, 0.5 }, 0.2, new[] { 0.005, 0.8, 0.2, 0.025, 0.4, 0.6 });
        Case("tr-pitch", new[] { 0.5, 0.5, 0.5, 0.5 }, 0.2, new[] { 0.010, 1.0, 0.9 });
        ProviderClean(); Quit();
        Console.WriteLine($"FRAMES {n} → {outPath}");
    }
}
