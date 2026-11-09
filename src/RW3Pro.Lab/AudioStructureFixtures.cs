using System;
using System.Collections.Generic;

namespace RW3ProNativeDSHaptics;

public sealed record AudioStructureTone(double Hz, double Level, double Phase = 0,
    int StartMs = 0, int EndMs = 1800, double PulseMs = 0, double ModulationHz = 0, double ChirpEndHz = 0);
public sealed record AudioStructureScene(string Id, AudioStructureTone[] Left, AudioStructureTone[] Right, int DriveMs = 1800);

/// <summary>Unnormalized deterministic source stimuli. No inferred/recorded controller output is fed back as input.</summary>
public static class AudioStructureFixtures
{
    public static IReadOnlyList<AudioStructureScene> Scenes()
    {
        var scenes = new List<AudioStructureScene>();
        void Both(string id, params AudioStructureTone[] tones) => scenes.Add(new(id, tones, (AudioStructureTone[])tones.Clone()));
        Both("structure-silence");
        foreach (double hz in new[] {30d,40,60,80,82,84,90,100,120,150,190,300,1000,4000,8000})
            Both($"structure-single-{hz}",new AudioStructureTone(hz,.4));
        Both("structure-same-cue-40-90",new(40,.3),new(90,.3));
        Both("structure-close-80-84",new(80,.3),new(84,.3));
        Both("structure-close-80-84-quarterphase",new(80,.3),new(84,.3,Phase:Math.PI/2));
        Both("structure-close-80-82",new(80,.3),new(82,.3));
        Both("structure-cross-cue-60-150",new(60,.3),new(150,.3));
        Both("structure-three-inside-40-90-120",new(40,.25),new(90,.25),new(120,.25));
        Both("structure-five-inside",new(40,.15),new(60,.15),new(80,.15),new(100,.15),new(120,.15));
        Both("structure-two-strong-weak-third",new(40,.3),new(120,.3),new(300,.045));
        Both("structure-mask-80-loud-120-soft",new(80,.5),new(120,.05));
        Both("structure-mask-80-soft-120-loud",new(80,.05),new(120,.5));
        Both("structure-am-80-8",new AudioStructureTone(80,.6,ModulationHz:8));
        Both("structure-hit5-150",new AudioStructureTone(150,.7,PulseMs:5));
        Both("structure-tone80-hit5-150",new(80,.3),new(150,.4,PulseMs:5));
        Both("structure-tone80-hit20-150",new(80,.3),new(150,.4,PulseMs:20));
        var a=new AudioStructureTone[]{new(40,.3),new(90,.3)};
        var b=new AudioStructureTone[]{new(60,.3),new(120,.3)};
        scenes.Add(new("structure-stereo-mixes",a,b));
        scenes.Add(new("structure-stereo-mixes-swapped",b,a));
        scenes.Add(new("structure-mix-left-only",a,[]));
        scenes.Add(new("structure-mix-right-only",[],a));
        scenes.Add(new("structure-opposite-80",[new(80,.5)],[new(80,.5,Phase:Math.PI)]));
        Both("structure-cancel-same-channel",new(80,.3),new(80,.3,Phase:Math.PI));
        Both("structure-pitch-step-80-120",new(80,.5,EndMs:900),new(120,.5,StartMs:900));
        Both("structure-chirp-30-200",new AudioStructureTone(30,.5,ChirpEndHz:200));
        Both("structure-low-high-80-1000",new(80,.3),new(1000,.3));
        Both("structure-voice-like-harmonics",new(120,.2),new(240,.12),new(360,.07),new(720,.03),new(1440,.02),new(2880,.01));
        Both("structure-near-bottom-30-40",new(30,.3),new(40,.3));
        return scenes;
    }

    public static float[] Generate(AudioStructureScene scene)
    {
        if(scene.DriveMs is < 100 or > 7000)throw new ArgumentException("Invalid structure duration.");
        Validate(scene.Left,scene.DriveMs);Validate(scene.Right,scene.DriveMs);
        var pcm=new float[(AudioReferenceFixtures.LeadMs+scene.DriveMs+AudioReferenceFixtures.TailMs)*48*2];
        for(int i=0;i<scene.DriveMs*48;i++)
        {
            int at=(AudioReferenceFixtures.LeadMs*48+i)*2;
            pcm[at]=(float)Sample(scene.Left,i/48000.0);
            pcm[at+1]=(float)Sample(scene.Right,i/48000.0);
        }
        return pcm;
    }
    public static double ConcurrentPeakBound(AudioStructureTone[] tones)
    {
        double maximum=0;
        foreach(var onset in tones)
        {
            double bound=0;
            foreach(var x in tones)if(x.StartMs<=onset.StartMs && x.EndMs>onset.StartMs)bound+=x.Level;
            maximum=Math.Max(maximum,bound);
        }
        return maximum;
    }
    private static double Sample(AudioStructureTone[] tones,double t)
    {
        double sum=0;
        foreach(var x in tones)
        {
            double start=x.StartMs/1000.0,end=x.EndMs/1000.0;
            if(t<start || t>=end)continue;
            double local=t-start;
            if(x.PulseMs>0 && local%.3>=x.PulseMs/1000)continue;
            double phase=x.Hz*local;
            if(x.ChirpEndHz>0)phase+=(x.ChirpEndHz-x.Hz)*local*local/(2*(end-start));
            double fade=Math.Clamp(Math.Min(local,end-t)/.005,0,1);
            double mod=x.ModulationHz>0?.5+.5*Math.Sin(2*Math.PI*x.ModulationHz*local):1;
            sum+=x.Level*Math.Sin(2*Math.PI*phase+x.Phase)*fade*mod;
        }
        return sum;
    }
    private static void Validate(AudioStructureTone[] tones,int duration)
    {
        if(tones==null || tones.Length>8)throw new ArgumentException("At most eight declared source tones per channel.");
        foreach(var x in tones)
        {
            if(x==null || !double.IsFinite(x.Hz) || x.Hz is < 20 or > 8000 || !double.IsFinite(x.Level) || x.Level is < 0 or > .9 ||
               !double.IsFinite(x.Phase) || x.StartMs<0 || x.EndMs>duration || x.StartMs>=x.EndMs ||
               !double.IsFinite(x.PulseMs) || x.PulseMs is < 0 or > 300 ||
               !double.IsFinite(x.ModulationHz) || x.ModulationHz is < 0 or > 100 ||
               !double.IsFinite(x.ChirpEndHz) || (x.ChirpEndHz!=0 && x.ChirpEndHz is < 20 or > 8000))
                throw new ArgumentException("Invalid declared source component.");
        }
        // No normalization: reject an unsafe declaration instead of changing the intended balance.
        if(ConcurrentPeakBound(tones)>.900000001)throw new ArgumentException("Declared simultaneous per-channel source peak bound exceeds .9.");
    }
}
