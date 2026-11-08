using System;

namespace RW3ProNativeDSHaptics;

public readonly record struct AcousticToneBand(double CenterHz, double AmplitudeRms, int Windows);

/// <summary>Bounded aggregate-only nominal-tone meter. No retained microphone PCM.
/// 100ms Hann windows, center +/-5Hz probes; max power in each triplet then mean across windows.
/// This is a descriptive acoustic band observation, not a calibrated actuator spectrum.</summary>
public sealed class AcousticToneBands
{
    private static readonly double[] Centers = { 40, 80, 120, 135, 190, 300 };
    private readonly double[] _coefficient = new double[18], _s1 = new double[18], _s2 = new double[18];
    private readonly double[] _sumPower = new double[6], _window;
    private readonly double _weightSum;
    private int _position, _windows;

    public AcousticToneBands(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _window = new double[sampleRate / 10];
        for (int i = 0; i < _window.Length; i++) { _window[i] = .5 - .5 * Math.Cos(2*Math.PI*i/_window.Length); _weightSum += _window[i]; }
        for (int c = 0; c < Centers.Length; c++) for (int j = 0; j < 3; j++)
            _coefficient[3*c+j] = 2*Math.Cos(2*Math.PI*(Centers[c]+5*(j-1))/sampleRate);
    }
    public void Reset() { Array.Clear(_s1);Array.Clear(_s2);Array.Clear(_sumPower);_position=_windows=0; }
    public void Push(float input)
    {
        double x = (float.IsFinite(input)?input:0)*_window[_position];
        for (int i=0;i<18;i++) { double next=x+_coefficient[i]*_s1[i]-_s2[i];_s2[i]=_s1[i];_s1[i]=next; }
        if (++_position<_window.Length)return;
        for(int c=0;c<6;c++) {
            double largest=0;
            for(int j=0;j<3;j++) { int i=3*c+j;double p=_s1[i]*_s1[i]+_s2[i]*_s2[i]-_coefficient[i]*_s1[i]*_s2[i];largest=Math.Max(largest,p); }
            _sumPower[c]+=4*largest/(_weightSum*_weightSum);
        }
        _windows++;_position=0;Array.Clear(_s1);Array.Clear(_s2);
    }
    public AcousticToneBand[] Read()
    {
        var result=new AcousticToneBand[6];
        for(int c=0;c<6;c++)result[c]=new(Centers[c],_windows>0?Math.Sqrt(_sumPower[c]/_windows):0,_windows);
        return result;
    }
}
