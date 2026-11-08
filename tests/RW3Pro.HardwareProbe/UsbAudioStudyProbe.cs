using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RW3ProNativeDSHaptics;

// Synapse route is source-only. Explicit pc-* routes use the production front-audio Direct USB path.
// No route opens a microphone, virtual controller or device-hiding interface.
internal static class UsbAudioStudyProbe
{
    public static async Task RunAsync(string directory, string suite, string route="synapse")
    {
        if (suite is not ("pilot" or "full" or "complex")) throw new ArgumentException("pilot, full or complex required");
        if(route is not ("synapse" or "pc-standard" or "pc-hybrid" or "pc-multiband"))throw new ArgumentException("Unknown study route.");
        if(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any())throw new InvalidOperationException("A fresh study directory is required.");
        Directory.CreateDirectory(directory);
        using var owner=new Mutex(true,"Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd",out bool acquired);
        if(!acquired)throw new InvalidOperationException("RW3 must remain fully closed during USB comparison.");
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int rate = endpoint.AudioClient.MixFormat.SampleRate;
        float volume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool mute = endpoint.AudioEndpointVolume.Mute;
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "setup.json"), JsonSerializer.Serialize(new {
            endpoint.ID, endpoint.FriendlyName, Format=endpoint.AudioClient.MixFormat.ToString(), rate, volume, mute,
            Suite=suite,Route=route, MicrophoneOpened=false, MotorWriterCreated=route!="synapse",
            SourceGainPercent=route=="synapse"?(int?)null:140,OutputLevelPercent=route=="synapse"?(int?)null:100,
            AudioSensitivityPercent=100,IntentionalSyncDelayMs=0,OtherAudioScreenIntervalMs=100,
            Limitation="Synthetic system audio, not genuine game-authored native haptic PCM. USB timestamps include playback scheduling."
        },options));
        var structures=AudioStructureFixtures.Scenes();
        IReadOnlyList<AudioReferenceScene> scenes = suite == "complex"
            ? structures.Select(x=>new AudioReferenceScene(x.Id,"declared-multitone",0,
                Math.Max(AudioStructureFixtures.ConcurrentPeakBound(x.Left),AudioStructureFixtures.ConcurrentPeakBound(x.Right)),
                x.Left.Length==0 && x.Right.Length>0?"right":x.Right.Length==0 && x.Left.Length>0?"left":"both",x.DriveMs)).ToArray()
            : suite == "pilot" ? AudioReferenceFixtures.Pilot() : AudioReferenceFixtures.Scenes();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Console.CancelKeyPress += (_, e) => {e.Cancel=true;cancel.Cancel();};
        var records = new List<object>();
        string? failure=null;
        SensaStereoPrototype? engine=null;
        AudioHapticsBridge? audio=null;
        void ValidateEnvironment()
        {
            using var current=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
            if(current.ID!=endpoint.ID || endpoint.AudioEndpointVolume.Mute!=mute || Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar-volume)>.001)
                throw new InvalidOperationException("Playback endpoint/volume/mute changed; study stopped.");
            if(audio?.Failure!=null)throw new InvalidOperationException(audio.Failure);
        }
        try {
            if(route!="synapse")
            {
                engine=new SensaStereoPrototype {Transport=SensaTransport.DirectUsb,SourceGainPercent=140,OutputLevelPercent=100,
                    OutputMethod=route switch {"pc-hybrid"=>SensaOutputMethod.Hybrid,"pc-multiband"=>SensaOutputMethod.Multiband,_=>SensaOutputMethod.Standard}};
                if(!engine.Start())throw new InvalidOperationException(engine.Status);
                for(int i=0;i<60 && !engine.IsActive && engine.IsRunning;i++)await Task.Delay(50,cancel.Token);
                if(!engine.IsActive)throw new InvalidOperationException(engine.Status);
                audio=new AudioHapticsBridge(new SensaAudioSink(engine.Features)){DelayOverrideMs=0};audio.Start(endpoint.ID);
            }
            for(int repeat=1;repeat<=(suite=="pilot"?1:3);repeat++) {
                var usb=UsbGamepadMonitor.Read();
                if(!usb.ScanSucceeded || !usb.WolverineWiredPcNode)throw new InvalidOperationException("PC-mode Wolverine unavailable at repeat boundary.");
                var order=scenes.ToArray();new Random(31000+repeat).Shuffle(order);
                foreach(var scene in order) {
                    cancel.Token.ThrowIfCancellationRequested();
                    ValidateEnvironment();
                    var definition=suite=="complex"?structures.Single(x=>x.Id==scene.Id):null;
                    var source=definition==null?AudioReferenceFixtures.Generate(scene):AudioStructureFixtures.Generate(definition);
                    double sumL=0,sumR=0;float peakL=0,peakR=0;
                    for(int i=0;i<source.Length;i+=2) {
                        sumL+=(double)source[i]*source[i];sumR+=(double)source[i+1]*source[i+1];
                        peakL=Math.Max(peakL,Math.Abs(source[i]));peakR=Math.Max(peakR,Math.Abs(source[i+1]));
                    }
                    byte[] bytes=new byte[source.Length*sizeof(float)];Buffer.BlockCopy(source,0,bytes,0,bytes.Length);
                    string sourceHash=Convert.ToHexString(SHA256.HashData(bytes));
                    if(rate!=48000) {
                        float[] converted=[];int count=new SincResampler(48000,rate).Process(source,ref converted);
                        bytes=new byte[count*sizeof(float)];Buffer.BlockCopy(converted,0,bytes,0,bytes.Length);
                    }
                    // Keep digital silence in the SAME stream after each stimulus, independently of a source stop.
                    Array.Resize(ref bytes,bytes.Length+rate*2*sizeof(float)*2);
                    using var memory=new MemoryStream(bytes,false);
                    using var wave=new RawSourceWaveStream(memory,WaveFormat.CreateIeeeFloatWaveFormat(rate,2));
                    using var output=new WasapiOut(endpoint,AudioClientShareMode.Shared,true,30);
                    var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    output.PlaybackStopped+=(_,e)=>{if(e.Exception!=null)stopped.TrySetException(e.Exception);else stopped.TrySetResult();};
                    output.Init(wave);
                    var start=DateTimeOffset.UtcNow;long tick=Stopwatch.GetTimestamp();
                    var otherAudio=new Dictionary<uint,float>();int meterReads=0,meterFailures=0;
                    void ObserveOtherAudio()
                    {
                        try {
                            var sessions=endpoint.AudioSessionManager.Sessions;
                            for(int j=0;j<sessions.Count;j++)
                            {
                                using var session=sessions[j];uint pid=session.GetProcessID;
                                if(pid==(uint)Environment.ProcessId)continue;
                                float peak=session.AudioMeterInformation.MasterPeakValue;
                                if(float.IsFinite(peak) && peak>0)otherAudio[pid]=Math.Max(otherAudio.GetValueOrDefault(pid),peak);
                            }
                            meterReads++;
                        } catch {meterFailures++;}
                    }
                    try {
                        output.Play();
                        while(!stopped.Task.IsCompleted) {
                            cancel.Token.ThrowIfCancellationRequested();ObserveOtherAudio();
                            if(Stopwatch.GetElapsedTime(tick)>TimeSpan.FromSeconds(15))throw new TimeoutException("Source playback timed out.");
                            await Task.WhenAny(stopped.Task,Task.Delay(100,cancel.Token));
                        }
                        await stopped.Task;
                    }
                    finally {output.Stop();}
                    ValidateEnvironment();
                    var record=new {Case=records.Count+1,Repeat=repeat,Scene=scene,SourceDefinition=definition,StartUtc=start,EndUtc=DateTimeOffset.UtcNow,
                        StopwatchTick=tick,StopwatchFrequency=Stopwatch.Frequency,SourceSha256=sourceHash,
                        PlaybackSha256=Convert.ToHexString(SHA256.HashData(bytes)),LeadMs=AudioReferenceFixtures.LeadMs,
                        SourcePeaks=new[]{peakL,peakR},SourceRms=new[]{Math.Sqrt(sumL/(source.Length/2)),Math.Sqrt(sumR/(source.Length/2))},
                        SilenceTailMs=AudioReferenceFixtures.TailMs+2000,Route=route,
                        OtherAudioSessionPeaks=otherAudio,OtherAudioMeterReads=meterReads,OtherAudioMeterFailures=meterFailures,
                        OtherAudioMayBePresent=otherAudio.Values.Any(x=>x>1e-5),
                        OtherAudioScreenIsNotContinuousProof=true,PhysicalEquivalenceProved=false};
                    records.Add(record);
                    File.WriteAllText(Path.Combine(directory,$"{records.Count:0000}-{scene.Id}-r{repeat}.json"),JsonSerializer.Serialize(record,options));
                    Console.WriteLine($"USB STUDY {records.Count}/{scenes.Count*(suite=="pilot"?1:3)} {scene.Id} r{repeat}");
                }
            }
        } catch(Exception ex) {failure=ex.ToString();Environment.ExitCode=1;Console.WriteLine(failure);}
        finally {
            try {audio?.Dispose();engine?.Stop();engine?.Dispose();}
            catch(Exception ex){failure=(failure??"")+" Output cleanup: "+ex;Environment.ExitCode=1;}
            File.WriteAllText(Path.Combine(directory,"completion.json"),JsonSerializer.Serialize(new {Completed=failure==null,Cases=records.Count,Failure=failure},options));
        }
    }
}
