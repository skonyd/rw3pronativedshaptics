using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RW3ProNativeDSHaptics;

internal static class AudioReferenceProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task RunAsync(string route, string directory, string microphoneId, string profile)
    {
        if (route is not ("synapse" or "disabled" or "pc" or "pc-hybrid" or "xbox")) throw new ArgumentException("Unknown reference route.");
        if (profile is not ("pilot" or "full" or "feel" or "stop")) throw new ArgumentException("Profile must be pilot, full, feel or stop.");
        Directory.CreateDirectory(directory);
        using var owner = new Mutex(true, "Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd", out bool acquired);
        if (!acquired) throw new InvalidOperationException("RW3 must remain fully closed for the comparison.");
        var usb = UsbGamepadMonitor.Read();
        if (!usb.ScanSucceeded || (route == "xbox" ? !usb.WolverineUsbNode || usb.WolverineWiredPcNode : !usb.WolverineWiredPcNode))
            throw new InvalidOperationException("Controller is not in the required PC/Xbox mode.");
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int playbackRate = endpoint.AudioClient.MixFormat.SampleRate;
        if (playbackRate is < 8000 or > 192000) throw new InvalidOperationException("Unsupported reference playback rate.");
        using var microphoneDevice = enumerator.GetDevice(microphoneId);
        float playbackVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool playbackMute = endpoint.AudioEndpointVolume.Mute;
        float micVolume = microphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        void ValidateEnvironment()
        {
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (current.ID != endpoint.ID || Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar - playbackVolume) > .001 ||
                endpoint.AudioEndpointVolume.Mute != playbackMute || Math.Abs(microphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar - micVolume) > .001 ||
                microphoneDevice.AudioEndpointVolume.Mute)
                throw new InvalidOperationException("Ses aygıtı/seviyesi veya mikrofon ayarı değişti; farklı koşullar aynı seriye katılmadı.");
        }
        Write(Path.Combine(directory, "setup.json"), new
        {
            Route = route, Profile = profile, Usb = usb, OS = Environment.OSVersion.ToString(),
            SourceEndpoint = endpoint.ID, endpoint.FriendlyName, Format = endpoint.AudioClient.MixFormat.ToString(),
            PlaybackMute = endpoint.AudioEndpointVolume.Mute, PlaybackVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar,
            SourceRate = AudioReferenceFixtures.Rate, PlaybackRate = playbackRate,
            PlaybackConversion = playbackRate == 48000 ? "none" : "explicit SincResampler 48k to endpoint rate, identical across routes",
            IntentionalSyncDelayMs = 0,
            Microphone = microphoneDevice.FriendlyName, MicMute = microphoneDevice.AudioEndpointVolume.Mute,
            MicVolume = microphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar,
            SourceGainPercent = 140, OutputLevelPercent = 100, AudioSensitivityPercent = 100,
            SynapseSettings = "User-selected Audio-to-Haptics, observed global/device level 6. For disabled/RW3 routes operator must turn Synapse Audio-to-Haptics OFF separately.",
            Conditions = "User confirms fixed Wolverine near mic and no external speaker sound. Microphone enhancements/volume unchanged; no raw mic PCM saved.",
            Limitation = "Single uncalibrated microphone; same sound is not physical fidelity or channel-isolation proof. No automatic success percentage."
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var token = cancellation.Token;
        IReadOnlyList<AudioReferenceScene> scenes = profile == "stop"
            ? [new("stop60", "tone", 60, .7, "both", 2000)]
            : profile == "feel"
            ? [new("feel60", "tone", 60, .9, "both", 7000), new("feel-hits", "pulse50", 120, .9, "both", 7000)]
            : profile == "pilot" ? AudioReferenceFixtures.Pilot() : AudioReferenceFixtures.Scenes();
        int repeats = profile == "feel" ? 1 : 3;
        var reports = new List<object>();
        string? failure = null;
        SensaStereoPrototype? engine = null;
        AudioHapticsBridge? audio = null;
        try
        {
            using var microphone = new MicrophoneLevelProbe(microphoneId, collectToneBands: true);
            await Task.Delay(1000, token);
            if (route is "pc" or "pc-hybrid")
            {
                engine = new SensaStereoPrototype { Transport = SensaTransport.DirectUsb, OutputLevelPercent = 100,
                    SourceGainPercent = 140, OutputMethod = route == "pc-hybrid" ? SensaOutputMethod.Hybrid : SensaOutputMethod.Standard };
                if (!engine.Start()) throw new InvalidOperationException(engine.Status);
                for (int i = 0; i < 60 && !engine.IsActive && engine.IsRunning; i++) await Task.Delay(50, token);
                if (!engine.IsActive) throw new InvalidOperationException(engine.Status);
                audio = new AudioHapticsBridge(new SensaAudioSink(engine.Features)) { DelayOverrideMs = 0 };
                audio.Start(endpoint.ID);
            }
            else if (route == "xbox")
            {
                var sink = new XboxAudioSink { OutputLevelPercent = 100 };
                if (!sink.HasGamepad) throw new InvalidOperationException("A unique connected XInput pad is required.");
                audio = new AudioHapticsBridge(sink) { DelayOverrideMs = 0 }; audio.Start(endpoint.ID);
            }
            // No Sensa/GameInput/XInput writer is constructed for synapse/disabled: pure external reference.
            await Task.Delay(1500, token);
            for (int repeat = 1; repeat <= repeats; repeat++)
            {
                var connected = UsbGamepadMonitor.Read();
                if (!connected.ScanSucceeded || (route == "xbox" ? !connected.WolverineUsbNode || connected.WolverineWiredPcNode : !connected.WolverineWiredPcNode))
                    throw new InvalidOperationException("Controller changed/disconnected during the reference run.");
                var order = scenes.ToArray();
                var random = new Random(28001 + repeat);
                random.Shuffle(order);
                foreach (var scene in order)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateEnvironment();
                    if (microphone.Failure != null || audio?.Failure != null) throw new InvalidOperationException(microphone.Failure ?? audio!.Failure);
                    float[] stereo = AudioReferenceFixtures.Generate(scene);
                    byte[] bytes = new byte[stereo.Length * sizeof(float)]; Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
                    string hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (playbackRate != AudioReferenceFixtures.Rate)
                    {
                        float[] converted = [];
                        int count = new SincResampler(AudioReferenceFixtures.Rate, playbackRate).Process(stereo, ref converted);
                        bytes = new byte[count * sizeof(float)]; Buffer.BlockCopy(converted, 0, bytes, 0, bytes.Length);
                    }
                    // Explicit five-second digital silence tail, with the output stream still open.
                    // Unlike pausing a video this proves our source itself is no longer emitting sound.
                    if (profile == "stop") Array.Resize(ref bytes, bytes.Length + playbackRate * 2 * sizeof(float) * 5);
                    string playbackHash = Convert.ToHexString(SHA256.HashData(bytes));
                    microphone.BeginWindow(scene.Hz); await Task.Delay(600, token);
                    var before = microphone.ReadWindow();
                    microphone.BeginWindow(scene.Hz);
                    var stopwatch = Stopwatch.StartNew();
                    await PlayAsync(bytes, endpoint, playbackRate, token);
                    var driven = microphone.ReadWindow();
                    microphone.BeginWindow(scene.Hz); await Task.Delay(400, token);
                    var after = microphone.ReadWindow();
                    ValidateEnvironment();
                    if (microphone.Failure != null || audio?.Failure != null) throw new InvalidOperationException(microphone.Failure ?? audio!.Failure);
                    var report = new { Scene = scene, Route = route, Repeat = repeat, SourceSha256 = hash, PlaybackSha256 = playbackHash,
                        Background = before, Driven = driven, After = after, ElapsedMs = stopwatch.Elapsed.TotalMilliseconds - 400,
                        AudioCallbacks = audio?.Callbacks, PhysicalFidelityVerified = false };
                    reports.Add(report);
                    Write(Path.Combine(directory, $"{reports.Count:0000}-{scene.Id}-r{repeat}.json"), report);
                    Console.WriteLine(JsonSerializer.Serialize(new { Case = reports.Count, Total = scenes.Count * repeats,
                        Route = route, Repeat = repeat, Scene = scene.Id, Background = before.BandRms, Driven = driven.BandRms,
                        MicClips = before.Clipped + driven.Clipped + after.Clipped }));
                }
            }
        }
        catch (Exception ex) { failure = ex.ToString(); Console.WriteLine("REFERENCE FAILED: " + ex.Message); Environment.ExitCode = 1; }
        finally
        {
            audio?.Dispose(); engine?.Stop(); engine?.Dispose();
            Write(Path.Combine(directory, "completion.json"), new { Completed = failure == null, Cases = reports.Count,
                Expected = scenes.Count * repeats, Failure = failure, PhysicalFidelityVerified = false });
        }
    }
    private static async Task PlayAsync(byte[] bytes, MMDevice endpoint, int rate, CancellationToken token)
    {
        using var memory = new MemoryStream(bytes, false);
        using var source = new RawSourceWaveStream(memory, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 30);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception != null) completed.TrySetException(e.Exception); else completed.TrySetResult(); };
        output.Init(source);
        try { output.Play(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(12), token); }
        finally { output.Stop(); }
    }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
