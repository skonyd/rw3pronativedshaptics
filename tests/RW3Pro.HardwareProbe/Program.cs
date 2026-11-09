using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using RW3ProNativeDSHaptics;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
if (args.FirstOrDefault() == "inspect-xbox-motors")
{
    using var input = new GameInputRumble(allowOutput: false);
    bool acquired = input.TryAcquire();
    Console.WriteLine(JsonSerializer.Serialize(new { acquired, input.SupportedMotorMask, input.LastEnumeration, input.LastError }));
    return;
}
if (args.FirstOrDefault() == "test-xbox-audio-combination")
{
    if (System.Diagnostics.Process.GetProcessesByName("RW3ProNativeDSHaptics").Length != 0)
        throw new InvalidOperationException("Close RW3 from its tray before the standalone motor test.");
    using var combinationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await XboxAudioGameBridge.RunSyntheticTestAsync(AudioHapticProfile.Existing, 65,
        new Progress<string>(Console.WriteLine), combinationTimeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Reports, result.Writes, PhysicalMotionVerified = false }));
    Environment.ExitCode = result.Reports > 0 && result.Writes > 0 ? 0 : 1;
    return;
}
if(args.FirstOrDefault()=="inspect-hidhide-nodes")
{
    Console.WriteLine(JsonSerializer.Serialize(HidHideControl.PadGameNodes(),jsonOptions));return;
}
if(args.FirstOrDefault()=="inspect-xinput-visibility")
{
    Console.WriteLine(JsonSerializer.Serialize(BodyFocusProbe.VisibleSlots()));return;
}
if(args.FirstOrDefault()=="inspect-gameinput-raw")
{
    using var input=new GameInputRumble(allowOutput:false);
    foreach(uint id in new uint[]{0,1,9})Console.WriteLine(input.InspectRawOutput(id));return;
}
if(args.FirstOrDefault()=="body-focus-probe")
{
    if(args.Length is <3 or >4)throw new ArgumentException("body-focus-probe <xinput|xinput-enabled|xinput-legacy|gameinput|wgi> <report.json> [1..120 seconds]");
    await BodyFocusProbe.RunAsync(args[1],Path.GetFullPath(args[2]),args.Length==4?int.Parse(args[3]):20);return;
}
if(args.FirstOrDefault()=="audio-seek-monitor")
{
    if(args.Length!=2)throw new ArgumentException("audio-seek-monitor <report.json>");
    await AudioSeekRecoveryProbe.MonitorAsync(Path.GetFullPath(args[1]));return;
}
if(args.FirstOrDefault()=="audio-seek-recovery")
{
    if(args.Length!=2)throw new ArgumentException("audio-seek-recovery <report.json>");
    await AudioSeekRecoveryProbe.RunAsync(Path.GetFullPath(args[1]));return;
}
if(args.FirstOrDefault()=="audio-structure-audit")
{
    if(args.Length!=2)throw new ArgumentException("audio-structure-audit <report-directory>");
    AudioStructureAudit.Run(Path.GetFullPath(args[1]));return;
}
if (args.FirstOrDefault() == "usb-audio-study")
{
    if(args.Length is <3 or >5)throw new ArgumentException("usb-audio-study <report-directory> <pilot|full|complex> [synapse|pc-standard|pc-hybrid|pc-multiband] [--synapse-off-confirmed]");
    string route=args.Length>=4?args[3]:"synapse";
    if(route!="synapse" && (args.Length!=5 || args[4]!="--synapse-off-confirmed"))throw new ArgumentException("RW3 route requires observed Synapse Audio-to-Haptics OFF, then --synapse-off-confirmed.");
    await UsbAudioStudyProbe.RunAsync(Path.GetFullPath(args[1]),args[2],route);return;
}
if (args.FirstOrDefault() == "audio-reference")
{
    if (args.Length != 5) throw new ArgumentException("audio-reference <synapse|disabled|pc|pc-hybrid|xbox> <report-directory> <microphone-id> <pilot|full>");
    await AudioReferenceProbe.RunAsync(args[1], Path.GetFullPath(args[2]), args[3], args[4]); return;
}
if(args.FirstOrDefault()=="frame-learn")
{
    if(args.Length!=3)throw new ArgumentException("frame-learn <sdk> <out.jsonl>");
    await FrameLearnProbe.RunAsync(PhysicalComparisonProbe.Resolve,Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));return;
}
if(args.FirstOrDefault()=="frame-sweep")
{
    if(args.Length!=3)throw new ArgumentException("frame-sweep <sdk> <out.jsonl>");
    FrameSweepProbe.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));return;
}
if(args.FirstOrDefault()=="direct-write")
{
    if(args.Length!=3)throw new ArgumentException("direct-write <mic-id> <report.json>");
    await DirectWriteProbe.RunAsync(args[1],Path.GetFullPath(args[2]));return;
}
if(args.FirstOrDefault()=="capture-pattern")
{
    if(args.Length<3)throw new ArgumentException("capture-pattern <sdk> <timeline.json> [wait-for-file]");
    await PhysicalComparisonProbe.CapturePatternAsync(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args.Length>3?args[3]:null);return;
}
if(args.FirstOrDefault()=="compare-methods")
{
    if(args.Length!=4)throw new ArgumentException("compare-methods <sdk> <directory> <mic-id>");
    await PhysicalComparisonProbe.MeasureAsync(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3],true);return;
}
if(args.FirstOrDefault()=="compare-physical")
{
    if(args.Length!=4)throw new ArgumentException("compare-physical <sdk> <directory> <mic-id>");
    await PhysicalComparisonProbe.MeasureAsync(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3]);return;
}
if(args.FirstOrDefault()=="compare-blind")
{
    if(args.Length!=5)throw new ArgumentException("compare-blind <sdk> <directory> <scene> <pair-number>");
    await PhysicalComparisonProbe.BlindPairAsync(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),args[3],int.Parse(args[4]));return;
}
if (args.FirstOrDefault() == "audio-endpoints")
{
    using var enumerator = new MMDeviceEnumerator();
    Console.WriteLine(JsonSerializer.Serialize(enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active)
        .Select(d => new {d.ID,d.FriendlyName,Format=d.AudioClient.MixFormat.ToString()}),jsonOptions));
    return;
}
if (args.FirstOrDefault() == "audit-spectrum")
{
    if (args.Length is < 2 or > 3) throw new ArgumentException("audit-spectrum <report.json> [2048|4096|0=multi-resolution]");
    SpectralAudit.Run(args[1], args.Length==3?int.Parse(args[2]):4096); return;
}
if (args.FirstOrDefault() == "probe-multiband-live")
{
    if (args.Length is < 3 or > 4) throw new ArgumentException("probe-multiband-live <licensed-sdk-directory> <report-directory> [microphone-id]");
    await MultibandLiveProbe.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args.Length==4?args[3]:null);
    return;
}
if (args.FirstOrDefault() is "probe-haps" or "probe-haps-update" or "probe-haps-generated")
{
    if (args.Length != 3) throw new ArgumentException("probe-haps <licensed-sdk-directory> <report-directory>");
    await HapsProviderProbe.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[0] == "probe-haps-update", args[0] == "probe-haps-generated");
    return;
}
if (args.FirstOrDefault() == "probe-bridge-input")
{
    if (args.Length != 2) throw new ArgumentException("Expected report filename.");
    // Input-only: do not acquire output ownership, change HidHide, or create a virtual device.
    using var input = new GameInputRumble(allowOutput: false);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    int acquired = 0, reads = 0, misses = 0, maxL = 0, maxR = 0, releasedL = 0, releasedR = 0;
    string error = "";
    Console.WriteLine("READ-ONLY input probe: pull and release L2/R2 for 20 seconds.");
    while (clock.Elapsed.TotalSeconds < 20)
    {
        if (input.TryAcquire())
        {
            acquired++;
            if (input.TryReadPad(out var pad))
            {
                reads++; maxL = Math.Max(maxL, pad.LeftTrigger); maxR = Math.Max(maxR, pad.RightTrigger);
                if (pad.LeftTrigger == 0) releasedL++; if (pad.RightTrigger == 0) releasedR++;
            }
            else { misses++; error = input.LastError; }
        }
        else { misses++; error = input.LastError; }
        await Task.Delay(8);
    }
    var result = new { acquired, reads, misses, maxL, maxR, releasedL, releasedR, error,
        ProductId = input.ProductId.ToString("X4"), input.LastEnumeration, MotorCommandsSent = input.Writes };
    File.WriteAllText(args[1], JsonSerializer.Serialize(result, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(result)); return;
}
if (args.FirstOrDefault() == "audit-xbox-safety")
{
    if (args.Length != 2) throw new ArgumentException("Expected report filename.");
    var writes = new List<Windows.Gaming.Input.GamepadVibration>();
    var sink = new XboxAudioSink(v => writes.Add(v));
    var delivery = new AudioSinkDelivery(sink);
    float[] signal = new float[4800 * 2];
    sink.Accept(signal);
    sink.Silence();
    bool idleNoWrites = writes.Count == 0;
    for (int i = 0; i < 4800; i++) { signal[2 * i] = .8f * MathF.Sin(2 * MathF.PI * 80 * i / 48000); signal[2 * i + 1] = signal[2 * i] * .7f; }
    delivery.Enqueue(signal, 0, 0); delivery.Pump(0);
    bool active = writes.Any(v => v.LeftMotor > 0 || v.RightMotor > 0 || v.LeftTrigger > 0 || v.RightTrigger > 0);
    bool bodyOnly = writes.All(v => v.LeftTrigger == 0 && v.RightTrigger == 0);
    int before = writes.Count; delivery.Pump(249); bool held = writes.Count == before;
    delivery.Pump(250);
    bool Zero(Windows.Gaming.Input.GamepadVibration v) => v.LeftMotor == 0 && v.RightMotor == 0 && v.LeftTrigger == 0 && v.RightTrigger == 0;
    bool zeroed = writes.Count > before && Zero(writes[^1]);
    delivery.Dispose(); int stopped = writes.Count;
    delivery.Enqueue(signal, 500, 0); delivery.Pump(500); bool noLateWrites = writes.Count == stopped;
    sink.Accept(new float[4800 * 2]); bool reset = Zero(writes[^1]);
    bool partialRejected = false;
    try { sink.Accept(new float[1]); } catch (ArgumentException) { partialRejected = true; }
    var malformedWrites = new List<Windows.Gaming.Input.GamepadVibration>();
    var malformedSink = new XboxAudioSink(v => malformedWrites.Add(v));
    malformedSink.Accept(Enumerable.Range(0, 480).Select(i => i % 2 == 0 ? float.NaN : float.PositiveInfinity).ToArray());
    bool invalidSilent = malformedSink.NonFiniteSamples == 480 && malformedWrites.All(Zero);
    malformedSink.Accept(signal);
    bool recovered = malformedWrites.Any(v => v.LeftMotor > 0);
    var levelWrites = new List<Windows.Gaming.Input.GamepadVibration>();
    var levelSink = new XboxAudioSink(v => levelWrites.Add(v));
    using var levelDelivery = new AudioSinkDelivery(levelSink);
    levelDelivery.Enqueue(signal,0,0); levelDelivery.Pump(0);
    bool initiallyActive = levelWrites.Any(v=>v.LeftMotor>0);
    levelDelivery.Configure(s=>((XboxAudioSink)s).OutputLevelPercent=0);
    levelDelivery.Enqueue(signal,10,0); levelDelivery.Pump(10);
    bool liveZero = Zero(levelWrites[^1]);
    levelDelivery.Configure(s=>((XboxAudioSink)s).OutputLevelPercent=50);
    levelDelivery.Enqueue(signal,20,0); levelDelivery.Pump(20);
    bool liveResume = levelWrites[^1].LeftMotor>0 && levelWrites[^1].LeftMotor<=.5;
    levelDelivery.Dispose(); int levelStopped=levelWrites.Count;
    levelDelivery.Configure(s=>((XboxAudioSink)s).OutputLevelPercent=100);
    bool levelSafe=initiallyActive && liveZero && liveResume && levelWrites.Count==levelStopped && levelWrites.All(v=>v.LeftTrigger==0 && v.RightTrigger==0);
    var result = new { IdleMakesNoWrites = idleNoWrites, ActiveMappingObserved = active, NoEarlySilence = held, AllFourChannelsZeroAtWatchdog = zeroed,
        NoPostStopWrite = noLateWrites, FilterHistoryCleared = reset, PartialStereoRejected = partialRejected,
        NonFiniteInputSilent = invalidSilent, ValidAudioRecoversAfterInvalidInput = recovered,
        AudioNeverMapsToTriggerChannels = bodyOnly, LiveLevelZeroResumeStopSafe = levelSafe, PhysicalXboxHardwareTested = false };
    File.WriteAllText(args[1], JsonSerializer.Serialize(result, jsonOptions)); Console.WriteLine(JsonSerializer.Serialize(result));
    Environment.ExitCode = idleNoWrites && active && held && zeroed && noLateWrites && reset && partialRejected && invalidSilent && recovered && bodyOnly && levelSafe ? 0 : 1;
    return;
}
if (args.FirstOrDefault() == "audit-software-scenarios")
{
    if (args.Length != 2) throw new ArgumentException("Expected report filename.");
    var comparisons = (from signal in HapticScenarioAudit.StressSignals()
        from rate in new[] { 48000, 44100, 96000 }
        let report = AudioPathEquivalence.Compare(signal, audioSourceRate: rate)
        select new { Rate = rate, Report = report, WithinTolerance = report.WithinTolerance(.01, 1, .02) }).ToArray();
    var result = new { PhaseLoss = HapticScenarioAudit.PhaseLoss(), MultitoneLoss = HapticScenarioAudit.MultitoneLoss(), UnscaledGainGrid = HapticScenarioAudit.GainGrid().ToArray(),
        Comparisons = comparisons, PhysicalFidelityVerified = false,
        Limit = "Equalized feature comparisons, unscaled clipping and independent original-PCM polarity check are separate. Sample-rate/short-pulse differences remain visible, never reclassified as physical success." };
    File.WriteAllText(args[1], JsonSerializer.Serialize(result, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(new { Cases = comparisons.Length, OutsideTolerance = comparisons.Count(x => !x.WithinTolerance), result.PhaseLoss, result.UnscaledGainGrid }));
    return;
}
if (args.FirstOrDefault() == "audit-microphone-graph")
{
    if (args.Length != 2) throw new ArgumentException("Expected report filename.");
    var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
        Windows.Media.Devices.MediaDevice.GetAudioCaptureSelector());
    var device = devices.Single(d => d.Name.Contains("A4tech", StringComparison.OrdinalIgnoreCase));
    var graphResult = await Windows.Media.Audio.AudioGraph.CreateAsync(
        new Windows.Media.Audio.AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Media));
    if (graphResult.Status != Windows.Media.Audio.AudioGraphCreationStatus.Success)
        throw new InvalidOperationException("AudioGraph creation: " + graphResult.Status);
    using var graph = graphResult.Graph;
    var inputResult = await graph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Media,
        graph.EncodingProperties, device);
    var result = new { Device = device.Name, Graph = graphResult.Status.ToString(),
        Input = inputResult.Status.ToString(), Error = inputResult.ExtendedError?.Message,
        Note = "Input access diagnostic only; no motor output, no audio file, no measured physical fidelity." };
    File.WriteAllText(args[1], JsonSerializer.Serialize(result, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(result));
    inputResult.DeviceInputNode?.Dispose();
    Environment.ExitCode = inputResult.Status == Windows.Media.Audio.AudioDeviceNodeCreationStatus.Success ? 0 : 1;
    return;
}
if (args.FirstOrDefault() == "audit-microphone-sta")
{
    if (args.Length < 3) throw new ArgumentException("Expected microphone endpoint ID and report filename.");
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var microphone = new MicrophoneLevelProbe(args[1]);
            Thread.Sleep(200);
            microphone.BeginWindow();
            Thread.Sleep(1000);
            var window = microphone.ReadWindow();
            File.WriteAllText(args[2], JsonSerializer.Serialize(new { Apartment = "STA", microphone.DeviceName, microphone.Failure, window }, jsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(new { Apartment = "STA", microphone.Failure, window.Samples, window.BandRms, window.Peak }));
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start(); thread.Join();
    if (failure != null)
    {
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { Apartment = "STA", Failure = failure.ToString(), Completed = false }, jsonOptions));
        Console.WriteLine(failure); Environment.ExitCode = 1;
    }
    return;
}
if (args.FirstOrDefault() == "audit-microphone")
{
    if (args.Length < 3) throw new ArgumentException("Expected microphone endpoint ID and report filename.");
    using var microphone = new MicrophoneLevelProbe(args[1], args.Contains("--mic-mme"));
    await Task.Delay(200);
    microphone.BeginWindow();
    await Task.Delay(1000);
    var window = microphone.ReadWindow();
    var result = new { microphone.DeviceName, microphone.Failure, CaptureApi = args.Contains("--mic-mme") ? "MME PCM16" : "WASAPI Float32", window };
    File.WriteAllText(args[2], JsonSerializer.Serialize(result, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(new { microphone.DeviceName, microphone.Failure, window.Samples, window.BandRms, window.Peak }));
    Environment.ExitCode = window.Samples > 0 && microphone.Failure == null ? 0 : 1;
    return;
}
if (args.FirstOrDefault() == "audit-software-grid")
{
    string path = args.Length == 2 ? Path.GetFullPath(args[1]) : throw new ArgumentException("Expected report directory.");
    Directory.CreateDirectory(path);
    foreach (int rate in new[] { 48000, 44100, 96000 })
    {
        var results = new List<AudioPathEquivalence.Report>();
        for (int hz = 30; hz <= 400; hz++)
            foreach (double level in new[] { .05, .2, .5, .9 })
            {
                double f = hz;
                var signal = new AudioPathEquivalence.Signal($"{hz}Hz/{level}", .3, t =>
                {
                    double x = t - .03;
                    double fade = Math.Clamp(Math.Min(x, .24 - x) / .01, 0, 1);
                    return (level * fade * Math.Sin(2 * Math.PI * f * x), 0);
                });
                results.Add(AudioPathEquivalence.Compare(signal, audioSourceRate: rate));
            }
        File.WriteAllText(Path.Combine(path, $"grid-{rate}.json"), JsonSerializer.Serialize(results, jsonOptions));
        if (results.Any(r => !r.WithinTolerance(.01, 1, .02))) Environment.ExitCode = 1;
        Console.WriteLine(JsonSerializer.Serialize(new { Rate = rate, Cases = results.Count,
            Failed = results.Count(r => !r.WithinTolerance(.01, 1, .02)),
            MaxAmplitudeError = results.Max(r => r.MaxAmpError), MaxFrequencyError = results.Max(r => r.MaxHzError),
            Note = "Equalized synthetic inputs; shared lossy features only. Not a Windows capture or physical output test." }));
    }
    return;
}
if (args.Length >= 2 && args[0] == "endpoint-settings")
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(args[1]);
    bool previousMute = device.AudioEndpointVolume.Mute;
    float previousVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
    if (args.Length == 4)
    {
        float volume = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        if (!float.IsFinite(volume) || volume < 0 || volume > 1) throw new ArgumentOutOfRangeException("volume");
        device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
        device.AudioEndpointVolume.Mute = bool.Parse(args[2]);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { device.FriendlyName, PreviousMute = previousMute, PreviousVolume = previousVolume,
        CurrentMute = device.AudioEndpointVolume.Mute, CurrentVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar }));
    return;
}
if (args.FirstOrDefault() == "--emit-channel-fixture")
{
    Environment.ExitCode = await EndpointFixturePlayer.RunCommandAsync(args);
    return;
}
if (args.Length >= 2 && args[0] == "unmute-microphone")
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(args[1]);
    if (device.DataFlow != DataFlow.Capture) throw new ArgumentException("A capture microphone is required.");
    bool previousMute = device.AudioEndpointVolume.Mute;
    device.AudioEndpointVolume.Mute = false;
    Console.WriteLine(JsonSerializer.Serialize(new { device.FriendlyName, PreviousMute = previousMute,
        CurrentMute = device.AudioEndpointVolume.Mute, GainUnchanged = device.AudioEndpointVolume.MasterVolumeLevelScalar }));
    return;
}
if (args.Length == 0 || args[0] == "inventory")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Usb = UsbGamepadMonitor.Read(), Microphones = ChannelLabRunner.Microphones(),
        Sources = NativeDualSenseCapture.EnumeratePossibleSources(),
        Scenes = DualSenseChannelFixtures.Scenes
    }, jsonOptions));
    return;
}
// Explicit CLI motor/microphone test. Shares the app's mutex to prevent two HAR owners.
using var owner = new Mutex(true, "Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd", out bool created);
if (!created) throw new InvalidOperationException("Close RW3 from its tray Exit command before running the hardware probe.");
string Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ??
    throw new ArgumentException("Required option: " + name);
string sdk = Path.GetFullPath(Option("--sdk-dir"));
string reportDir = Path.GetFullPath(Option("--report-dir"));
Directory.CreateDirectory(reportDir);
var presence = UsbGamepadMonitor.Read();
if (!presence.ScanSucceeded || !presence.WolverineWiredPcNode)
    throw new InvalidOperationException("Wolverine wired PC mode 1532:0A4C is required for this hardware probe.");
NativeLibrary.SetDllImportResolver(typeof(SensaStereoPrototype).Assembly, (name, assembly, search) =>
    name is "HAR" or "Interhaptics.RazerProvider" ? NativeLibrary.Load(Path.Combine(sdk, name + ".dll")) : IntPtr.Zero);
var microphones = ChannelLabRunner.Microphones();
string micSearch = Option("--mic");
var matches = microphones.Where(m => m.Id == micSearch || m.Name.Contains(micSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
if (matches.Length != 1) throw new ArgumentException("Microphone must match exactly one listed endpoint.");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(args[0] is "audit-sweep" or "audit-session" ? 15 : 8));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
string[] scenes = args[0] switch
{
    "audit-controls" or "audit-sweep" or "audit-repeat" or "audit-extra" or "audit-session" or "audit-session-check" or "audit-headroom" or "audit-multiband" or "audit-cadence" => [],
    "run-controls" => ["silence", "speaker", "left", "right", "stereo", "alternating", "transients"],
    "run-demos" => DualSenseChannelFixtures.Scenes.Where(s => s.Demo != null).Select(s => s.Id).ToArray(),
    "run" => [Option("--scene")],
    "run-cancel" => ["demo:Rocket"],
    "run-endpoint" => [Option("--scene")],
    "run-endpoint-controls" => ["silence", "speaker", "left", "right", "stereo", "alternating", "transients"],
    _ => throw new ArgumentException("Use inventory, run-controls, run-demos, or run --scene ID")
};
int levelIndex = Array.IndexOf(args, "--level");
int outputLevel = levelIndex >= 0 ? int.Parse(args[levelIndex + 1]) : 100;
if (outputLevel is < 0 or > 100) throw new ArgumentOutOfRangeException("--level");
var metadata = new
{
    TimestampUtc = DateTime.UtcNow, OS = Environment.OSVersion.ToString(), Firmware = "not recorded",
    Microphone = matches[0], Usb = presence, SdkDirectory = sdk,
    SdkHashes = new[] { "HAR.dll", "Interhaptics.RazerProvider.dll" }.ToDictionary(n => n,
        n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sdk, n))))),
    Setup = args[0].StartsWith("run-endpoint", StringComparison.Ordinal)
        ? "Physical DualSense USB audio endpoint used for a synthetic source. No physical DualSense reference/fidelity comparison."
        : "User reports Wolverine near microphone, Xbox button uppermost. DualSense not tested.",
    Limitation = "Acoustic aggregate levels only, not side-specific sensing or calibrated haptic fidelity. No microphone PCM saved."
};
File.WriteAllText(Path.Combine(reportDir, "setup.json"), JsonSerializer.Serialize(metadata, jsonOptions));
if (args[0] == "audit-cadence")
{
    int originalTick = SensaStereoPrototype.TickMs, index = 0;
    try {
        for(int repeat=1;repeat<=2;repeat++)
        foreach(int tick in repeat==1?new[]{36,33,30}:new[]{30,33,36})
        foreach(bool multiband in repeat==1?new[]{false,true}:new[]{true,false}) {
            SensaStereoPrototype.TickMs=tick;
            Console.WriteLine($"CADENCE {tick}ms multiband={multiband} repeat={repeat}");
            var report=await SingleMicrophoneAudit.RunAsync(new("cadence-80",80,.35,false,3000),"reference",repeat,
                matches[0].Id,Option("--playback"),timeout.Token,experimentalMultiband:multiband);
            File.WriteAllText(Path.Combine(reportDir,$"audit-{index++:000}.json"),JsonSerializer.Serialize(report,jsonOptions));
        }
    }
    finally { SensaStereoPrototype.TickMs=originalTick; }
    return;
}
if (args[0] == "audit-session")
{
    int index = 0;
    foreach (bool keepEngine in new[] { false, true })
    foreach (bool keepMicrophone in new[] { false, true })
    {
        using var engine = keepEngine ? new SensaStereoPrototype { OutputLevelPercent = 100, SourceGainPercent = 140 } : null;
        using var microphone = keepMicrophone ? new MicrophoneLevelProbe(matches[0].Id) : null;
        for (int repeat = 1; repeat <= 6; repeat++)
        foreach (string route in repeat % 2 == 1 ? new[] { "reference", "audio" } : new[] { "audio", "reference" })
        {
            Console.WriteLine($"SESSION {index} keepEngine={keepEngine} keepMicrophone={keepMicrophone} repeat={repeat} route={route}");
            var report = await SingleMicrophoneAudit.RunAsync(new("session-80", 80, .5, false, 1000), route, repeat,
                matches[0].Id, Option("--playback"), timeout.Token, sharedEngine: engine, sharedMicrophone: microphone);
            File.WriteAllText(Path.Combine(reportDir, $"audit-{index++:000}.json"), JsonSerializer.Serialize(report, jsonOptions));
        }
    }
    return;
}
if (args[0].StartsWith("audit-", StringComparison.Ordinal))
{
    string playback = Option("--playback");
    var tone = new AcousticAuditScene("control-80", 80, .5, false, 1000);
    var cases = new List<(AcousticAuditScene Scene, string Route, int Repeat)>();
    using var multibandEngine = args[0] == "audit-multiband" ? new SensaStereoPrototype {ExperimentalMultiband=true,SourceGainPercent=100,OutputLevelPercent=100} : null;
    if (args[0] == "audit-multiband")
        for(int repeat=1;repeat<=2;repeat++)
        foreach(string pattern in new[]{"tone","mixed"})
        foreach(string route in repeat==1?new[]{"reference","audio"}:new[]{"audio","reference"})
            cases.Add((new("multiband-80-"+pattern,80,.4,false,1000,pattern),route,repeat));
    else if (args[0] == "audit-controls")
        for (int repeat = 1; repeat <= 2; repeat++)
            foreach (string route in new[] { "idle", "off-audio", "reference", "audio" }) cases.Add((tone, route, repeat));
    else if (args[0] == "audit-headroom")
        for (int repeat = 1; repeat <= 2; repeat++)
            foreach (double level in new[] { .5, .9 })
                foreach (string route in repeat == 1 ? new[] { "reference", "audio" } : new[] { "audio", "reference" })
                    cases.Add((new($"unity-80-{level}", 80, level, false, 1000), route, repeat));
    else if (args[0] == "audit-session-check")
        for (int repeat = 1; repeat <= 6; repeat++)
            foreach (string route in repeat % 2 == 1 ? new[] { "reference", "audio" } : new[] { "audio", "reference" })
                cases.Add((new("session-80", 80, .5, false, 1000), route, repeat));
    else if (args[0] == "audit-sweep")
        foreach (var scene in SingleMicrophoneAudit.SweepScenes())
            foreach (string route in new[] { "reference", "audio" }) cases.Add((scene, route, 1));
    else if (args[0] == "audit-extra")
    {
        foreach (var scene in new AcousticAuditScene[]
        {
            new("diagnostic-50", 50, .5, false, 1000),
            new("diagnostic-80-low", 80, .2, false, 1000),
            new("diagnostic-120", 120, .5, false, 1000),
            new("stereo-80-190", 80, .5, false, 1000, "stereo"),
            new("bursts-120", 120, .5, false, 1000, "bursts"),
            new("alternating-80", 80, .5, false, 1000, "alternating"),
            new("sustain-80", 80, .5, false, 5000)
        })
            foreach (string route in new[] { "reference", "audio" }) cases.Add((scene, route, 1));
        foreach (string route in new[] { "cancel-reference", "cancel-audio" })
            cases.Add((new("cancel-80", 80, .5, false, 5000), route, 1));
    }
    else
        for (int repeat = 1; repeat <= 3; repeat++)
            foreach (var signal in new[] { (Hz: 50d, Level: .5), (Hz: 80d, Level: .2), (Hz: 80d, Level: .5), (Hz: 190d, Level: .5) })
                foreach (string route in new[] { "reference", "audio" })
                    cases.Add((new($"repeat-{signal.Hz}-{signal.Level}", signal.Hz, signal.Level, false, 1000), route, repeat));
    int index = 0;
    foreach (var item in cases)
    {
        Console.WriteLine($"START {index} {item.Scene.Name} {item.Route} repeat={item.Repeat}");
        AcousticAuditReport report;
        try { report = await SingleMicrophoneAudit.RunAsync(item.Scene, item.Route, item.Repeat, matches[0].Id, playback, timeout.Token, args.Contains("--mic-mme"),sharedEngine:multibandEngine); }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(reportDir, $"audit-{index:000}-failed.json"), JsonSerializer.Serialize(new
            { item.Scene, item.Route, Failure = ex.ToString(), PhysicalFidelityVerified = false, Completed = false }, jsonOptions));
            throw;
        }
        File.WriteAllText(Path.Combine(reportDir, $"audit-{index++:000}.json"), JsonSerializer.Serialize(report, jsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { report.Scene, report.Route, report.Repeat,
            Background = report.Background.BandRms, Active = report.DrivenAndTail.BandRms,
            report.AudioCallbacks, report.ClippedAudioSamples, report.AudioFailure }));
    }
    return;
}
foreach (string id in scenes)
{
    var scene = DualSenseChannelFixtures.Scenes.Single(s => s.Id == id);
    Console.WriteLine("START " + scene.Id);
    try
    {
        // Fixed and disclosed Wolverine gain; the original fixture is not scaled.
        if (args[0].StartsWith("run-endpoint", StringComparison.Ordinal))
        {
            var endpointReport = await EndpointLabRunner.RunAsync(scene, Option("--endpoint"), outputLevel, 140,
                matches[0].Id, timeout.Token);
            File.WriteAllText(Path.Combine(reportDir, id.Replace(':', '-') + ".json"), JsonSerializer.Serialize(endpointReport, jsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(endpointReport));
            continue;
        }
        using var scenarioStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        bool cancelTest = args[0] == "run-cancel";
        if (cancelTest) scenarioStop.CancelAfter(3000); // baseline + warm-up, then cancel during real output
        var report = await ChannelLabRunner.ReplayAsync(scene, outputLevel, 140, matches[0].Id, scenarioStop.Token);
        if (cancelTest) throw new InvalidOperationException("Expected cancellation did not occur.");
        File.WriteAllText(Path.Combine(reportDir, id.Replace(':', '-') + ".json"), JsonSerializer.Serialize(report, jsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
    catch (OperationCanceledException) when (args[0] == "run-cancel" && !timeout.IsCancellationRequested)
    {
        File.WriteAllText(Path.Combine(reportDir, "cancel.json"), JsonSerializer.Serialize(new
        {
            ExpectedCancellation = true, CompletedPlayback = false,
            Note = "Cancellation returned after engine.Stop cleanup. Physical stop still requires observation."
        }, jsonOptions));
        Console.WriteLine("Expected cancellation completed; engine cleanup returned.");
    }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(reportDir, id.Replace(':', '-') + "-failed.json"),
            JsonSerializer.Serialize(new { Scene = id, Failure = ex.ToString(), Completed = false }, jsonOptions));
        throw;
    }
}
