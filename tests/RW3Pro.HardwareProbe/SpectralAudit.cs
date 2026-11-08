using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RW3ProNativeDSHaptics;

internal static class SpectralAudit
{
    public static void Run(string path, int windowSamples = 4096)
    {
        var analyzer=new HapticSpectrumAnalyzer(windowSamples);
        float[] Wave(double hz,double level,double phase)=>Enumerable.Range(0,windowSamples).Select(i=>(float)(level*Math.Sin(2*Math.PI*hz*i/48000+phase))).ToArray();
        var rows=new List<object>();int failed=0;double maxHz=0,maxAmp=0;
        foreach(double hz in new[]{30d,31,35,40,50,60,80,81.3,120,150,190,250,300,350,399,400})
        foreach(double level in new[]{.01,.05,.2,.5,.9})
        foreach(double phase in new[]{0d,.7,Math.PI}) {
            var result=analyzer.Analyze(Wave(hz,level,phase));
            double frequencyError=Math.Abs(result.A.FrequencyHz-hz),amplitudeError=Math.Abs(result.A.Amplitude-level);
            bool pass=result.Count==1&&frequencyError<=1&&amplitudeError<=.025;
            if(!pass)failed++;maxHz=Math.Max(maxHz,frequencyError);maxAmp=Math.Max(maxAmp,amplitudeError);
            rows.Add(new{Hz=hz,Level=level,Phase=phase,Result=result,FrequencyError=frequencyError,AmplitudeError=amplitudeError,WithinTolerance=pass});
        }
        var pcm=Wave(80,.3,0);var durations=new double[400];
        for(int i=0;i<20;i++)analyzer.Analyze(pcm);
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<durations.Length;i++){long start=Stopwatch.GetTimestamp();analyzer.Analyze(pcm);durations[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;}
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;Array.Sort(durations);
        var resultObject=new{Cases=rows,Failed=failed,MaxFrequencyError=maxHz,MaxAmplitudeError=maxAmp,
            MonoWindowMilliseconds=new{Median=durations[200],P95=durations[380],Maximum=durations[^1]},AllocatedBytes=allocated,
            SourceHistoryMs=windowSamples/48d,SelectedBinSeparationHz=3*48000d/windowSamples,PhysicalFidelityVerified=false,
            Limit="Finite original-PCM analyzer oracle, not physical fidelity or phase output. Resolution depends on window/level/spacing; 80+84 is unresolved. Runtime timing is this machine's warmed single-mono FFT, not full bridge CPU or game latency."};
        File.WriteAllText(path,JsonSerializer.Serialize(resultObject,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{Cases=rows.Count,Failed=failed,MaxHz=maxHz,MaxAmplitude=maxAmp,resultObject.MonoWindowMilliseconds,AllocatedBytes=allocated}));
        Environment.ExitCode=failed==0?0:1;
    }
}
