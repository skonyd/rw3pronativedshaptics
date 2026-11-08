using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RW3ProNativeDSHaptics;

internal static class PhysicalComparisonProbe
{
    private static readonly string[] Scenes={"single80","single190","mixed80-190","short-hits","near80-84","stereo80-190"};
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static byte[] Source(string scene, int driveMs=1600)
    {
        if(!Scenes.Contains(scene))throw new ArgumentException("Unknown comparison scene.");
        byte[] pcm=new byte[(driveMs+400)*48*8];
        for(int i=0;i<pcm.Length/8;i++) {
            double t=i/48000d-.2;
            if(t<0||t>=driveMs/1000d)continue;
            double fade=Math.Clamp(Math.Min(t,driveMs/1000d-t)/.01,0,1);
            double S(double hz)=>Math.Sin(2*Math.PI*hz*t);
            double l=scene switch {
                "single80"=>.5*S(80),"single190"=>.5*S(190),
                "mixed80-190"=>.25*(S(80)+S(190)),"near80-84"=>.25*(S(80)+S(84)),
                "short-hits"=>t%.25<.02?.5*Math.Sin(2*Math.PI*120*(t%.25)):0,
                "stereo80-190"=>.35*S(80),_=>0};
            double r=scene=="stereo80-190"?.35*S(190):0;
            short left=(short)Math.Round(l*fade*32767),right=(short)Math.Round(r*fade*32767);
            pcm[i*8+4]=(byte)left;pcm[i*8+5]=(byte)(left>>8);
            pcm[i*8+6]=(byte)right;pcm[i*8+7]=(byte)(right>>8);
        }
        return pcm;
    }
    private static void Resolve(string sdk)
    {
        var usb=UsbGamepadMonitor.Read();
        if(!usb.ScanSucceeded||!usb.WolverineWiredPcNode)throw new InvalidOperationException("Wired PC Wolverine1532:0A4C required.");
        NativeLibrary.SetDllImportResolver(typeof(SensaStereoPrototype).Assembly,(name,_,_)=>
            name is "HAR" or "Interhaptics.RazerProvider"?NativeLibrary.Load(Path.Combine(sdk,name+".dll")):IntPtr.Zero);
    }
    private static async Task Start(SensaStereoPrototype engine,CancellationToken token)
    {
        if(!engine.Start())throw new InvalidOperationException(engine.Status);
        for(int i=0;i<40&&!engine.IsActive&&engine.IsRunning;i++)await Task.Delay(50,token);
        if(!engine.IsActive)throw new InvalidOperationException(engine.Status);
    }
    /// <summary>
    /// Known Sensa pattern for a USB capture of the vendor transport: waits for a marker file written
    /// by the elevated capture script, then plays timed steps and logs their UTC times (pcap clock).
    /// </summary>
    public static async Task CapturePatternAsync(string sdk,string timelinePath,string? waitForFile)
    {
        using var owner=new Mutex(true,"Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd",out bool created);
        if(!created)throw new InvalidOperationException("Close RW3 via tray Exit first.");
        Resolve(sdk);
        if(waitForFile!=null){var w=System.Diagnostics.Stopwatch.StartNew();while(!File.Exists(waitForFile)){if(w.Elapsed.TotalMinutes>3)throw new TimeoutException("capture did not start");await Task.Delay(200);}}
        await Task.Delay(1500);
        var steps=new List<object>();
        using var engine=new SensaStereoPrototype{SourceGainPercent=100,OutputLevelPercent=100};
        await Start(engine,CancellationToken.None);
        void Mark(string name)=>steps.Add(new{Step=name,Utc=DateTime.UtcNow.ToString("O")});
        Mark("engine-started-idle");await Task.Delay(2000);
        async Task Manual(string name,float l,float r){Mark(name+"-start");var sw=System.Diagnostics.Stopwatch.StartNew();while(sw.ElapsedMilliseconds<1000){engine.Submit(l,r);await Task.Delay(5);}engine.Submit(0,0);Mark(name+"-end");await Task.Delay(1500);}
        await Manual("left-0.25",.25f,0);
        await Manual("left-1.00",1f,0);
        await Manual("right-1.00",0,1f);
        await Manual("both-0.50",.5f,.5f);
        async Task Tone(string name,double hz,double amp){
            Mark(name+"-start");var buf=new float[480];long n=0;var sw=System.Diagnostics.Stopwatch.StartNew();
            while(n<48000){for(int i=0;i<240;i++){buf[2*i]=(float)(amp*Math.Sin(2*Math.PI*hz*(n+i)/48000.0));buf[2*i+1]=0;}engine.Features.PushStereo(buf);n+=240;while(sw.Elapsed.TotalMilliseconds<n/48.0)await Task.Delay(1);}
            Mark(name+"-end");await Task.Delay(1500);}
        await Tone("tone-80hz-left",80,.5);
        await Tone("tone-190hz-left",190,.5);
        Mark("pattern-done");engine.Stop();
        File.WriteAllText(timelinePath,JsonSerializer.Serialize(new{TickMs=SensaStereoPrototype.TickMs,Steps=steps},Json));
        Console.WriteLine("PATTERN done: "+timelinePath);
    }

    public static async Task MeasureAsync(string sdk,string directory,string micId,bool threeMethods=false)
    {
        Directory.CreateDirectory(directory);
        using var owner=new Mutex(true,"Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd",out bool created);
        if(!created)throw new InvalidOperationException("Close RW3 via tray Exit first.");
        Resolve(sdk);
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
        var reports=new List<object>();string? failure=null;
        try {
            using var mic=new MicrophoneLevelProbe(micId,collectToneBands:true);
            await Task.Delay(1000,stop.Token);
            File.WriteAllText(Path.Combine(directory,"setup.json"),JsonSerializer.Serialize(new{Usb=UsbGamepadMonitor.Read(),
                OS=Environment.OSVersion.ToString(),Firmware="not recorded",Microphone=mic.DeviceName,
                SourceGain=100,OutputLevel=100,TickMs=SensaStereoPrototype.TickMs,
                Setup="User confirmed controller fixed near A4tech, quiet room. Mic processing/volume unchanged. No Sony output or raw mic recording.",
                SdkHashes=new[]{"HAR.dll","Interhaptics.RazerProvider.dll"}.ToDictionary(n=>n,n=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sdk,n)))))},Json));
            for(int repeat=1;repeat<=5;repeat++)
            for(int sceneIndex=0;sceneIndex<Scenes.Length;sceneIndex++)
            if(Environment.GetEnvironmentVariable("RW3_SCENES") is not string only || only.Split(',').Contains(Scenes[sceneIndex]))
            foreach(var method in Order(repeat,sceneIndex,threeMethods)) {
                bool multi=method==SensaOutputMethod.Multiband;
                string scene=Scenes[sceneIndex];byte[] pcm=Source(scene);string hash=Convert.ToHexString(SHA256.HashData(pcm));
                mic.BeginWindow();await Task.Delay(400,stop.Token);var background=mic.ReadWindow();
                using var engine=new SensaStereoPrototype{OutputMethod=method,SourceGainPercent=100,OutputLevelPercent=100};
                await Start(engine,stop.Token);await Task.Delay(1000,stop.Token); // Predeclared settling, never fitted to match.
                mic.BeginWindow();await Task.Delay(400,stop.Token);var idle=mic.ReadWindow();
                mic.BeginWindow();
                await ChannelLabRunner.ReplaySourceAsync(pcm,engine,stop.Token);
                await Task.Delay(1100,stop.Token);var driven=mic.ReadWindow();
                bool active=engine.IsActive;long updates=engine.MultibandFrames,reductions=engine.MultibandHeadroomReductions;
                long hybridMulti=engine.HybridMultiFrames,hybridStandard=engine.HybridStandardFrames;
                engine.Stop();mic.BeginWindow();await Task.Delay(400,stop.Token);var off=mic.ReadWindow();
                if(!active||mic.Failure!=null||driven.Samples==0||driven.TruncatedBins>0||engine.IsRunning)
                    throw new InvalidOperationException("Measurement incomplete: "+engine.Status+" / "+mic.Failure);
                bool unchanged=hash==Convert.ToHexString(SHA256.HashData(pcm));
                if(!unchanged)throw new InvalidOperationException("Original source mutated.");
                var report=new{Scene=scene,Repeat=repeat,Multiband=multi,Method=method.ToString(),HybridMultiFrames=hybridMulti,HybridStandardFrames=hybridStandard,SourceHash=hash,SourceUnchanged=unchanged,
                    Background=background,EngineIdle=idle,Driven=driven,EngineOff=off,Updates=updates,HeadroomReductions=reductions,
                    WorkerStopped=!engine.IsRunning,PhysicalFidelityVerified=false,
                    Limit="One processed/uncalibrated airborne mic. Nominal100ms tone bands are not calibrated motor spectrum. No absolute latency, phase or physical DualSense comparison. Same original source and100% gain/output; no loudness normalization."};
                File.WriteAllText(Path.Combine(directory,$"case-{reports.Count:000}.json"),JsonSerializer.Serialize(report,Json));reports.Add(report);
                Console.WriteLine($"MEASURE {reports.Count}/{(threeMethods?90:60)} {scene} {method} repeat={repeat} complete");
            }
        }
        catch(Exception ex){failure=ex.ToString();throw;}
        finally {
            Console.CancelKeyPress-=cancel;
            File.WriteAllText(Path.Combine(directory,"completion.json"),JsonSerializer.Serialize(new{Completed=reports.Count==(threeMethods?90:60),Cases=reports.Count,Failure=failure,PhysicalFidelityVerified=false},Json));
        }
    }

    /// <summary>Counterbalanced method order: two methods alternate (legacy run); three rotate (Latin square).</summary>
    private static SensaOutputMethod[] Order(int repeat,int sceneIndex,bool three)
    {
        if(!three)return (repeat+sceneIndex)%2==0?new[]{SensaOutputMethod.Standard,SensaOutputMethod.Multiband}:new[]{SensaOutputMethod.Multiband,SensaOutputMethod.Standard};
        var all=new[]{SensaOutputMethod.Standard,SensaOutputMethod.Hybrid,SensaOutputMethod.Multiband};
        int k=(repeat+sceneIndex)%3;
        return new[]{all[k],all[(k+1)%3],all[(k+2)%3]};
    }

    public static async Task BlindPairAsync(string sdk,string directory,string scene,int pair)
    {
        Directory.CreateDirectory(directory);
        using var owner=new Mutex(true,"Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd",out bool created);
        if(!created)throw new InvalidOperationException("Close RW3 via tray Exit first.");
        Resolve(sdk);
        bool firstMulti=RandomNumberGenerator.GetInt32(2)==1;
        // Key is revealed only after subjective answers are recorded.
        File.WriteAllText(Path.Combine(directory,$"pair-{pair:00}-key.json"),JsonSerializer.Serialize(new{Pair=pair,Scene=scene,FirstMultiband=firstMulti,Gain=100,Output=100},Json));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for(int slot=1;slot<=2;slot++) {
            bool multi=slot==1?firstMulti:!firstMulti;
            using var engine=new SensaStereoPrototype{ExperimentalMultiband=multi,SourceGainPercent=100,OutputLevelPercent=100};
            await Start(engine,stop.Token);await Task.Delay(1000,stop.Token);
            Console.WriteLine($"PAIR {pair} SLOT {slot} START");
            await ChannelLabRunner.ReplaySourceAsync(Source(scene,2800),engine,stop.Token);
            engine.Stop();Console.WriteLine($"PAIR {pair} SLOT {slot} END");
            if(engine.IsRunning)throw new InvalidOperationException("Stop not complete.");
            await Task.Delay(1200,stop.Token);
        }
    }
}
