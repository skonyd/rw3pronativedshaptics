using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RW3ProNativeDSHaptics;

internal static class MultibandLiveProbe
{
    public static async Task RunAsync(string sdk, string folder, string? micId = null)
    {
        Directory.CreateDirectory(folder);
        using var owner = new Mutex(true,"Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd",out bool created);
        if(!created)throw new InvalidOperationException("Close RW3 from tray Exit first.");
        var usb=UsbGamepadMonitor.Read();
        if(!usb.ScanSucceeded || !usb.WolverineWiredPcNode)throw new InvalidOperationException("Wired PC Wolverine required.");
        NativeLibrary.SetDllImportResolver(typeof(SensaStereoPrototype).Assembly,(name,_,_)=>
            name is "HAR" or "Interhaptics.RazerProvider" ? NativeLibrary.Load(Path.Combine(sdk,name+".dll")) : IntPtr.Zero);
        var reports=new List<object>();string? failure=null;
        using var microphone=micId!=null?new MicrophoneLevelProbe(micId):null;
        if(microphone!=null)await Task.Delay(1000);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();}; Console.CancelKeyPress+=cancel;
        try {
            for(int repeat=1;repeat<=2;repeat++)
            foreach(int side in new[]{0,1})
            foreach(var tones in new[]{new[]{80d},new[]{190d},new[]{80d,190d},new[]{40d,120d,300d}}) {
                using var engine=new SensaStereoPrototype {ExperimentalMultiband=true,CaptureFrames=true,SourceGainPercent=100,OutputLevelPercent=100};
                if(!engine.Start())throw new InvalidOperationException(engine.Status);
                for(int wait=0;wait<40&&!engine.IsActive&&engine.IsRunning;wait++)await Task.Delay(50,stop.Token);
                if(!engine.IsActive)throw new InvalidOperationException(engine.Status);
                byte[] pcm=new byte[48000*8];
                for(int i=0;i<48000;i++) {
                    double t=i/48000d;
                    double fade=Math.Clamp(Math.Min(t-.15,.75-t)/.01,0,1);
                    double value=tones.Sum(hz=>.12*Math.Sin(2*Math.PI*hz*t))*fade;
                    short sample=(short)Math.Round(value*32767); int p=i*8+4+side*2;
                    pcm[p]=(byte)sample;pcm[p+1]=(byte)(sample>>8);
                }
                string hash=Convert.ToHexString(SHA256.HashData(pcm));
                microphone?.BeginWindow(80);if(microphone!=null)await Task.Delay(350,stop.Token);
                var background=microphone?.ReadWindow();microphone?.BeginWindow(80);
                var frames=new List<string>();
                var replay=ChannelLabRunner.ReplaySourceAsync(pcm,engine,stop.Token);
                while(!replay.IsCompleted) {
                    byte[] frame=engine.LastFrame;if(frame.Length>0)frames.Add(Convert.ToHexString(frame));
                    await Task.Delay(33,stop.Token);
                }
                await replay;await Task.Delay(200,stop.Token);
                var driven=microphone?.ReadWindow();
                bool active=engine.IsActive;long multiband=engine.MultibandFrames;
                string status=engine.Status; engine.Stop();
                microphone?.BeginWindow(80);if(microphone!=null)await Task.Delay(600,stop.Token);
                var afterStop=microphone?.ReadWindow();
                bool unchanged=hash==Convert.ToHexString(SHA256.HashData(pcm));
                reports.Add(new{Repeat=repeat,Side=side,Tones=tones,SourceHash=hash,SourceUnchanged=unchanged,
                    ActiveAtEnd=active,MultibandFrames=multiband,engine.MultibandHeadroomReductions,Frames=frames,Status=status,Stopped=!engine.IsRunning,
                    Background=background,Driven=driven,AfterStop=afterStop,MicrophoneFailure=microphone?.Failure,
                    MicrophoneProcessing="Windows audio enhancement settings unchanged; this is not an unprocessed/calibrated spectrum comparison"});
                Console.WriteLine($"LIVE multiband repeat={repeat} side={side} tones={string.Join('+',tones)} frames={multiband} active={active}");
                if(!active||multiband==0||!unchanged||microphone?.Failure!=null||microphone!=null&&(driven?.Samples==0||afterStop?.Samples==0))throw new InvalidOperationException("Live experimental backend failed: "+status);
            }
        }
        catch(Exception ex){failure=ex.ToString();throw;}
        finally {
            Console.CancelKeyPress-=cancel;
            File.WriteAllText(Path.Combine(folder,"live-results.json"),JsonSerializer.Serialize(new{Cases=reports,Failure=failure,Completed=reports.Count==16,
                Usb=usb,Firmware="not recorded",OS=Environment.OSVersion.ToString(),
                PhysicalFidelityVerified=false,Limit="Real original-PCM ingress to experimental FFT/UpdateHM/provider, bounded synthetic output. No microphone or physical DualSense comparison; frame differences do not prove motor spectrum or phase."},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
}
