using System.IO;
using System.Text.Json;
using RW3ProNativeDSHaptics;

/// <summary>Source/extractor/policy audit only. Does not open microphone, audio output or USB.</summary>
internal static class AudioStructureAudit
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var json=new JsonSerializerOptions{WriteIndented=true};
        var summary=new List<object>();
        foreach(var scene in AudioStructureFixtures.Scenes())
        {
            var pcm=AudioStructureFixtures.Generate(scene);
            var extractor=new HapticFeatureExtractor();extractor.EnableSpectralAnalysis(true);
            long cursor=0;
            var policies=new[]{new HybridOutputPolicy(),new HybridOutputPolicy()};
            var frames=new List<object>();
            int multi=0,steady=0,weakThirdVeto=0;
            for(int offset=0;offset<pcm.Length;)
            {
                int count=Math.Min(33*48*2,pcm.Length-offset);
                extractor.PushStereo(pcm.AsSpan(offset,count));offset+=count;
                int slots=extractor.Take(ref cursor,out var left,out var right);
                bool have=extractor.TryTakeSpectrum(out var ls,out var rs);
                double at=offset/96000.0;
                object Side(SideFeatures f,HapticSpectrum s,int side)
                {
                    bool hybrid=policies[side].Decide(f,s,have,at);
                    var standard=HybridOutputPolicy.StandardFrame(f,s,have);
                    float[] strengths=Enumerable.Range(0,s.Count).Select(i=>s[i].Amplitude).OrderByDescending(a=>a).ToArray();
                    bool strongPair=strengths.Length>=2 && strengths[1]>=strengths[0]*HybridOutputPolicy.SecondComponentRatio;
                    bool veto=have && strongPair && !HybridOutputPolicy.IsMixed(s);
                    if(at>.65 && at<1.6) {steady++;if(hybrid)multi++;if(veto)weakThirdVeto++;}
                    return new {f.Peak,Points=new[]{f.P0,f.P1,f.P2,f.P3},f.FrequencyHz,f.TransientCount,
                        Spectrum=Enumerable.Range(0,s.Count).Select(i=>s[i]).ToArray(),
                        StandardPitch=f.FrequencyHz,HybridUsesMultiple=hybrid,HybridSinglePitch=standard.FrequencyHz,
                        StrongPairVetoedByThird=veto,PhaseIsNotAMotorCommand=true};
                }
                frames.Add(new {AtSeconds=at,Slots=slots,SpectrumAvailable=have,Left=Side(left,ls,0),Right=Side(right,rs,1)});
            }
            var report=new {Scene=scene,SourceRate=48000,DeclaredSourcePeakBound=.9,
                SpectralHistoryMs=4096/48.0,MinimumPeakSeparationHz=HapticSpectrumAnalyzer.MinimumSeparationHz,
                SpectrumPeakLimit=3,FrameRequestMs=33,HardwareOpened=false,PhysicalFidelityProved=false,
                Limitation="Production feature extractor and Hybrid policy simulated on a sample timeline. Not actual USB delivery, not Synapse equivalence. Phase and original waveform are not motor outputs.",Frames=frames};
            File.WriteAllText(Path.Combine(directory,scene.Id+".json"),JsonSerializer.Serialize(report,json));
            summary.Add(new {scene.Id,SteadySideFrames=steady,HybridMultiSideFrames=multi,StrongPairVetoedSideFrames=weakThirdVeto});
        }
        File.WriteAllText(Path.Combine(directory,"summary.json"),JsonSerializer.Serialize(summary,json));
        Console.WriteLine(JsonSerializer.Serialize(summary,json));
    }
}
