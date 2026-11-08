using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RW3ProNativeDSHaptics;

// Explicit, isolated diagnostic. RenderHapticsDebug also delivers frames to the real provider.
internal static class HapsProviderProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Target { public int Sign, Group, Side; }
    private static class Har
    {
        [DllImport("HAR")] public static extern bool Init();
        [DllImport("HAR")] public static extern void Quit();
        [DllImport("HAR")] public static extern int AddHM([MarshalAs(UnmanagedType.LPStr)] string content);
        [DllImport("HAR")] public static extern bool UpdateHM(int id, [MarshalAs(UnmanagedType.LPStr)] string content);
        [DllImport("HAR")] public static extern void AddTargetToEventMarshal(int id, [In] Target[] targets, int count);
        [DllImport("HAR")] public static extern void SetEventIntensity(int id, double value);
        [DllImport("HAR")] public static extern void PlayEvent(int id, double v, double t, double s);
        [DllImport("HAR")] public static extern void StopAllEvents();
        [DllImport("HAR")] public static extern void ComputeAllEvents(double time);
    }
    private static class Provider
    {
        [DllImport("Interhaptics.RazerProvider")] public static extern bool ProviderInit();
        [DllImport("Interhaptics.RazerProvider")] public static extern bool ProviderIsPresent();
        [DllImport("Interhaptics.RazerProvider")] public static extern bool ProviderClean();
        [DllImport("Interhaptics.RazerProvider")] public static extern void ProviderRenderHaptics();
        [DllImport("Interhaptics.RazerProvider")] public static extern int RenderHapticsDebug([Out] byte[] buffer, int index, int size);
    }

    // Project-authored constants using the publicly published v5 schema, not vendor effect assets.
    private static string MakeHaps((double Hz, double Amp)[] tones)
    {
        object Curve(double value) => new { m_extrapolationStrategy = 0,
            m_keyframes = new[] { new { m_time = 0d, m_value = value }, new { m_time = 2d, m_value = value } } };
        object Melody((double Hz, double Amp)? tone) => new { m_gain = 1d, m_mute = 0,
            m_notes = tone is { } t ? new object[] { new { m_gain = 1d, m_startingPoint = 0d,
                m_length = 2d, m_priority = 1, m_hapticEffect = new {
                    m_amplitudeModulation = Curve(t.Amp), m_frequencyModulation = Curve(t.Hz) } } } : Array.Empty<object>() };
        object Perception(object[] melodies) => new { m_gain = 1d, m_loop = 0, m_maximum = 1d,
            m_signalEvaluationMethod = 3, m_melodies = melodies };
        return JsonSerializer.Serialize(new { m_HDFlag = 0, m_description = "RW3 synthetic multitone provider probe",
            m_gain = 1d, m_length_unit = 7, m_time_unit = 0, m_version = "5",
            m_stiffness = Perception([]), m_texture = Perception([]),
            m_vibration = Perception(new[] { Melody(null) }.Concat(tones.Select(t => Melody(t))).ToArray()) });
    }

    public static async Task RunAsync(string sdkDirectory, string reportDirectory, bool update = false, bool generated = false)
    {
        Directory.CreateDirectory(reportDirectory);
        using var owner = new Mutex(true, "Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd", out bool created);
        if (!created) throw new InvalidOperationException("Close RW3 using tray Exit before this probe.");
        var usb = UsbGamepadMonitor.Read();
        if (!usb.ScanSucceeded || !usb.WolverineWiredPcNode) throw new InvalidOperationException("Wired PC Wolverine 1532:0A4C required.");
        NativeLibrary.SetDllImportResolver(typeof(HapsProviderProbe).Assembly, (name, _, _) =>
            name is "HAR" or "Interhaptics.RazerProvider" ? NativeLibrary.Load(Path.Combine(sdkDirectory, name + ".dll")) : IntPtr.Zero);
        var cases = new (string Name, (double Hz, double Amp)[] Tones)[] {
            ("silence", [(80,0)]), ("80", [(80,.15)]), ("190", [(190,.15)]),
            ("80-plus-190", [(80,.15),(190,.15)]), ("190-plus-80", [(190,.15),(80,.15)]),
            ("135-at-summed-level", [(135,.30)]), ("80-strong-190-weak", [(80,.20),(190,.10)]),
            ("116-666-at-summed-level", [(116.6666666667,.30)]),
            ("three-tones", [(40,.10),(120,.10),(300,.10)]) };
        var results = new List<object>(); byte[] scratch = new byte[256];
        bool initialized = false, provider = false; string? failure = null;
        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            initialized = Har.Init();
            if (!initialized) throw new InvalidOperationException("HAR Init failed.");
            provider = Provider.ProviderInit();
            if (!provider || !Provider.ProviderIsPresent()) throw new InvalidOperationException("Razer provider absent.");
            for (int repeat = 1; repeat <= 2; repeat++)
            foreach (int side in new[] { 2, 1 })
            foreach (var item in repeat == 1 ? cases : cases.Reverse().ToArray())
            {
                stop.Token.ThrowIfCancellationRequested(); Har.StopAllEvents();
                string haps = MakeHaps(item.Tones);
                if (generated) {
                    var c = item.Tones.Select(t => new HapticSpectralComponent((float)t.Hz,(float)t.Amp,0)).ToArray();
                    haps = MultibandHaps.Build(new(c.Length>0?c[0]:default,c.Length>1?c[1]:default,c.Length>2?c[2]:default,c.Length),
                        new SideFeatures {P0=1,P1=1,P2=1,P3=1},1,out _);
                }
                File.WriteAllText(Path.Combine(reportDirectory, item.Name + ".haps"), haps);
                int id = Har.AddHM(haps);
                if (id < 0) throw new InvalidOperationException("Documented haps schema rejected: " + item.Name);
                if (update && !Har.UpdateHM(id, haps)) throw new InvalidOperationException("UpdateHM rejected.");
                Har.AddTargetToEventMarshal(id, [new Target { Sign = 1, Group = 302, Side = side }], 1);
                Har.SetEventIntensity(id, 1); Har.PlayEvent(id, -clock.Elapsed.TotalSeconds, 0, 0);
                var frames = new List<string>();
                for (int frame = 0; frame < 6; frame++)
                {
                    if (!Provider.ProviderIsPresent()) throw new InvalidOperationException("Provider lost.");
                    Har.ComputeAllEvents(clock.Elapsed.TotalSeconds);
                    int size = Provider.RenderHapticsDebug(scratch, 0, scratch.Length);
                    if (size < 1 || size > scratch.Length) throw new InvalidOperationException("Invalid provider frame length: " + size);
                    frames.Add(Convert.ToHexString(scratch.AsSpan(0, size)));
                    await Task.Delay(33, stop.Token);
                }
                results.Add(new { item.Name, Side = side, Repeat = repeat, Frames = frames });
                Console.WriteLine($"HAPS {repeat} side={side} {item.Name}: {frames[^1]}");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (initialized) {
                try { Har.StopAllEvents(); if (provider) for (int i = 0; i < 4; i++) { Har.ComputeAllEvents(clock.Elapsed.TotalSeconds); Provider.ProviderRenderHaptics(); await Task.Delay(33); } }
                catch (Exception ex) { failure ??= "Cleanup: " + ex; Console.WriteLine("Cleanup failed: " + ex.Message); }
                finally { try { if (provider) Provider.ProviderClean(); } catch (Exception ex) { failure ??= "Provider cleanup: " + ex; } Har.Quit(); }
            }
            File.WriteAllText(Path.Combine(reportDirectory, "provider-results.json"), JsonSerializer.Serialize(new {
                Usb = usb, Firmware = "not recorded", OS = Environment.OSVersion.ToString(),
                SdkHashes = new[] { "HAR.dll", "Interhaptics.RazerProvider.dll" }.ToDictionary(n => n,
                    n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sdkDirectory,n))))),
                Cases = results, UpdateHMTested = update, Failure = failure, Completed = results.Count == cases.Length * 4, PhysicalFidelityVerified = false,
                Limit = "Explicit synthetic .haps effects, 0.30 maximum summed authored amplitude, 33ms debug render delivers to provider. Raw frame evidence only, not decoded independent bands, game source, calibrated physical spectrum or phase." }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
