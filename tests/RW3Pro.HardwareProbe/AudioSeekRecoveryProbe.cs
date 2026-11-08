using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RW3ProNativeDSHaptics;

// Windows WASAPI source/client replacement test. No controller writes or microphone.
internal static class AudioSeekRecoveryProbe
{
    public static async Task MonitorAsync(string path)
    {
        using var enumerator=new MMDeviceEnumerator();
        using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        double left=0,right=0;
        var sink=new XboxAudioSink(v=>{Volatile.Write(ref left,v.LeftMotor);Volatile.Write(ref right,v.RightMotor);});
        using var bridge=new AudioHapticsBridge(sink){DelayOverrideMs=0};
        var rows=new List<object>();
        bridge.Start(device.ID);
        for(int i=0;i<60;i++)
        {
            await Task.Delay(1000);
            float? peak=null;string? current=null;
            try{peak=device.AudioMeterInformation.MasterPeakValue;using var d=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);current=d.ID;}catch{}
            rows.Add(new {Seconds=i+1,Utc=DateTimeOffset.UtcNow,bridge.Callbacks,bridge.HasRecentSound,bridge.IsCapturing,bridge.Failure,
                EndpointPeak=peak,DefaultEndpoint=current,CaptureEndpoint=device.ID,ExpectedBodyLeft=Volatile.Read(ref left),ExpectedBodyRight=Volatile.Read(ref right)});
            File.WriteAllText(path,JsonSerializer.Serialize(new {MotorWriterCreated=false,RawAudioRecorded=false,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine("Read-only audio monitor completed; no controller writes.");
    }
    private sealed class Sink : IAudioHapticSink
    {
        private long _samples;
        public long AudibleSamples=>Interlocked.Read(ref _samples);
        public string Description=>"source recovery diagnostic (no motors)";
        public void Accept(ReadOnlySpan<float> pcm) {long n=0;foreach(float x in pcm)if(float.IsFinite(x)&&Math.Abs(x)>.003)n++;Interlocked.Add(ref _samples,n);}
        public void Silence() { }
    }
    public static async Task RunAsync(string path)
    {
        using var enumerator=new MMDeviceEnumerator();
        using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        int rate=device.AudioClient.MixFormat.SampleRate;
        var sink=new Sink();using var bridge=new AudioHapticsBridge(sink){DelayOverrideMs=0};
        var cases=new List<object>();string? failure=null;
        try {
            bridge.Start(device.ID);
            for(int i=0;i<18;i++)
            {
                // Recreate the render client and jump to source sample zero while capture stays alive.
                byte[] bytes=new byte[rate*2*4/2];
                for(int n=0;n<rate/2;n++){
                    float v=.08f*MathF.Sin(2*MathF.PI*(i%2==0?80:190)*n/rate);
                    BitConverter.TryWriteBytes(bytes.AsSpan(n*8,4),v);BitConverter.TryWriteBytes(bytes.AsSpan(n*8+4,4),v);
                }
                long before=sink.AudibleSamples;
                using var memory=new MemoryStream(bytes,false);
                using var source=new RawSourceWaveStream(memory,WaveFormat.CreateIeeeFloatWaveFormat(rate,2));
                using var output=new WasapiOut(device,AudioClientShareMode.Shared,true,30);
                var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped+=(_,e)=>{if(e.Exception==null)done.TrySetResult();else done.TrySetException(e.Exception);};
                output.Init(source);output.Play();
                try{await done.Task.WaitAsync(TimeSpan.FromSeconds(5));}finally{output.Stop();}
                await Task.Delay(100);
                bool resumed=bridge.IsCapturing && bridge.Failure==null && sink.AudibleSamples>before;
                cases.Add(new {Index=i+1,GapMs=i%3==0?450:40,Resumed=resumed,bridge.Callbacks,bridge.Failure});
                if(!resumed)throw new InvalidOperationException("No audible capture after source-client replacement.");
                await Task.Delay(i%3==0?450:40);
            }
        }catch(Exception ex){failure=ex.ToString();Environment.ExitCode=1;}
        finally{
            bridge.Stop();
            File.WriteAllText(path,JsonSerializer.Serialize(new {Completed=failure==null,Cases=cases,Failure=failure,
                Endpoint=device.FriendlyName,Rate=rate,PhysicalMotorsTested=false,YouTubeReproduction=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"Source replacement cases {cases.Count}/18; failure: {failure??"none"}");
    }
}
