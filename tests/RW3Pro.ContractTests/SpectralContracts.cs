using RW3ProNativeDSHaptics;
using System.Text.Json;

internal static class SpectralContracts
{
    public static void Run(Action<bool,string> check)
    {
        var meter=new AcousticToneBands(48000);
        for(int i=0;i<48000;i++)meter.Push((float)(.2*Math.Sin(2*Math.PI*80*i/48000)+.15*Math.Sin(2*Math.PI*190*i/48000)));
        var bands=meter.Read();
        check(Math.Abs(bands.Single(b=>b.CenterHz==80).AmplitudeRms-.2)<.001 &&
            Math.Abs(bands.Single(b=>b.CenterHz==190).AmplitudeRms-.15)<.001 && bands.All(b=>b.Windows==10),
            "Aggregate-only microphone tone meter distinguishes known80/190 components without retaining PCM");
        meter.Reset();for(int i=0;i<4800;i++)meter.Push(0);
        check(meter.Read().All(b=>b.AmplitudeRms==0),"Acoustic nominal-band window reset cannot leak an earlier tone");
        var analyzer = new HapticSpectrumAnalyzer();
        float[] Wave(params (double Hz, double Amplitude, double Phase)[] tones) =>
            Enumerable.Range(0,HapticSpectrumAnalyzer.WindowSamples).Select(i =>
                (float)tones.Sum(t => t.Amplitude * Math.Sin(2*Math.PI*t.Hz*i/48000 + t.Phase))).ToArray();
        bool tonesAccurate = true; double maxFrequency = 0, maxAmplitude = 0;
        foreach (double hz in new[] {30d,40,50,80,120,190,250,300,400})
        foreach (double level in new[] {.05,.2,.5,.9})
        foreach (double phase in new[] {0d,.6,Math.PI}) {
            var result = analyzer.Analyze(Wave((hz,level,phase)));
            maxFrequency = Math.Max(maxFrequency,Math.Abs(result.A.FrequencyHz-hz));
            maxAmplitude = Math.Max(maxAmplitude,Math.Abs(result.A.Amplitude-level));
            tonesAccurate &= result.Count == 1 && Math.Abs(result.A.FrequencyHz-hz)<1 && Math.Abs(result.A.Amplitude-level)<.025;
        }
        check(tonesAccurate,$"Spectral single-tone oracle: frequency <=1Hz, amplitude <=.025 across 108 level/phase cases (max {maxFrequency:F4}Hz/{maxAmplitude:F4})");
        foreach (var tones in new[] { new[]{(80d,.25,0d),(190d,.25,.7)}, new[]{(80d,.4,1d),(190d,.1,2d)}, new[]{(40d,.2,0d),(120d,.2,.6),(300d,.2,Math.PI)} }) {
            var pcm = Wave(tones); var copy = (float[])pcm.Clone(); var result = analyzer.Analyze(pcm);
            bool accurate = result.Count == tones.Length && tones.All(t => Enumerable.Range(0,result.Count).Any(i => Math.Abs(result[i].FrequencyHz-t.Item1)<1 && Math.Abs(result[i].Amplitude-t.Item2)<.015));
            check(accurate,"Independent authored multitone components recovered: " + string.Join("+",tones.Select(t=>t.Item1)));
            check(Enumerable.Range(1,result.Count-1).All(i=>result[i-1].FrequencyHz<result[i].FrequencyHz),
                "Resolved spectral components keep stable low-to-high melody order");
            check(pcm.SequenceEqual(copy),"Spectral analysis does not modify original PCM");
        }
        var positive = analyzer.Analyze(Wave((80,.4,0))); var negative = analyzer.Analyze(Wave((80,.4,Math.PI)));
        double delta = Math.Abs(Math.IEEERemainder(positive.A.PhaseRadians-negative.A.PhaseRadians,2*Math.PI));
        check(Math.Abs(delta-Math.PI)<.001,"Original-window polarity phase remains distinguishable in analysis (not a motor phase command)");
        var close = analyzer.Analyze(Wave((80,.25,0),(84,.25,0)));
        check(close.Count<2,"Unresolved 4Hz-separated tones are not reported as two confidently resolved carriers");
        check(analyzer.Analyze(new float[4096]).Count==0 && analyzer.Analyze(Enumerable.Repeat(float.NaN,4096).ToArray()).Count==0,
            "Silent/nonfinite spectrum cannot manufacture a carrier");
        var features = new HapticFeatureExtractor(); features.EnableSpectralAnalysis(true);
        var stereo = new float[8192]; var left = Wave((80,.3,0));
        for(int i=0;i<4096;i++)stereo[2*i]=left[i];
        features.PushStereo(stereo.AsSpan(0,4000));
        check(!features.TryTakeSpectrum(out _,out _),"Experimental spectrum explicitly waits for a complete 85.33ms window");
        features.PushStereo(stereo.AsSpan(4000));
        check(features.TryTakeSpectrum(out var ls,out var rs) && ls.Count==1 && rs.Count==0,"Original spectral history preserves left/right isolation across uneven packets");
        features.ClearForSilence();
        check(!features.TryTakeSpectrum(out _,out _),"Silence discards spectral history instead of replaying old tones");
        var envelope = new SideFeatures {P0=1,P1=1,P2=1,P3=1};
        var spectrum = new HapticSpectrum(new(80,.6f,0),new(190,.6f,0),default,2);
        using var haps=JsonDocument.Parse(MultibandHaps.Build(spectrum,envelope,2,out bool reduced));
        var melodies=haps.RootElement.GetProperty("m_vibration").GetProperty("m_melodies");
        double total=0;
        for(int i=1;i<melodies.GetArrayLength();i++)total+=melodies[i].GetProperty("m_notes")[0].GetProperty("m_hapticEffect").GetProperty("m_amplitudeModulation").GetProperty("m_keyframes")[0].GetProperty("m_value").GetDouble();
        check(reduced && Math.Abs(total-1)<1e-7 && melodies.GetArrayLength()==3,"Documented .haps keeps simultaneous melodies and applies one shared headroom scale");
        bool boundedTail=true;
        for(int i=1;i<melodies.GetArrayLength();i++) {
            var keys=melodies[i].GetProperty("m_notes")[0].GetProperty("m_hapticEffect").GetProperty("m_amplitudeModulation").GetProperty("m_keyframes");
            foreach(var key in keys.EnumerateArray())if(key.GetProperty("m_time").GetDouble()>=.066)boundedTail&=key.GetProperty("m_value").GetDouble()==0;
        }
        check(boundedTail,"Experimental notes request zero amplitude after66ms even if the next update is missing");
        string a=MultibandHaps.Build(positive,envelope,1,out _), b=MultibandHaps.Build(negative,envelope,1,out _);
        check(!a.Contains("PhaseRadians") && !b.Contains("PhaseRadians"),"Experimental output makes no unsupported phase-command claim");
        analyzer.Analyze(left); long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++)analyzer.Analyze(left);
        check(GC.GetAllocatedBytesForCurrentThread()==before,"Warmed FFT analysis allocates no managed memory per window");
    }
}
