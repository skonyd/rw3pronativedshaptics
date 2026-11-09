using System;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>Features of one actuator for one engine tick. Plain struct, no allocation.</summary>
public struct SideFeatures
{
    /// <summary>Envelope (0..1) at 4 evenly spaced points across the tick window.</summary>
    public float P0, P1, P2, P3;
    /// <summary>Single-band frequency in Hz over the last40ms, 0 when unknown/silent.</summary>
    public float FrequencyHz;
    /// <summary>Up to two onsets inside the window: position 0..1 and amplitude 0..1.</summary>
    public int TransientCount;
    public float T0Position, T0Amplitude, T1Position, T1Amplitude;

    public readonly float Peak => Math.Max(Math.Max(P0, P1), Math.Max(P2, P3));
}

/// <summary>
/// v2 Native analyser: turns the DualSense rear haptic channels (3/4, 48 kHz) into the
/// parameters the Wolverine stream actually carries — per actuator an amplitude curve
/// (4 points per frame), one frequency and up to 2 transients (Razer metadata "Sage T1":
/// 3 bands / 4 points / 2 transients; the public parametric API reaches one band).
///
/// Producer: audio capture thread (<see cref="Push"/>). Consumer: Sensa engine tick
/// (<see cref="Take"/>). Work is split into 1 ms slots; nothing allocates after construction.
/// The source PCM is only read, never modified.
/// </summary>
public sealed class HapticFeatureExtractor
{
    public const int SampleRate = 48000;
    public const int SlotSamples = SampleRate / 1000;      // 1 ms
    public const int RingSlots = 512;                      // 512 ms history
    public const int FrequencyWindowSlots = 40;            // 40 ms: >= 1 period at 30 Hz
    public const float ZeroCrossThreshold = 0.004f;        // hysteresis floor, ≈ -48 dBFS
    /// <summary>
    /// Hysteresis also scales with the envelope, so the decaying ringing tail after an abrupt
    /// stop (which sits right at the fixed floor) is not counted as cycles. 2026-11-08: with the
    /// fixed floor alone, 190 Hz taps gated at 12 Hz read 2.8–5.7 Hz differently on two
    /// equally valid paths (one crossing more or less).
    /// </summary>
    public const float ZeroCrossRelative = 0.10f;
    public const float TransientRatio = 5f;                // slot energy vs running mean
    public const float TransientFloor = 2e-4f;             // ≈ -37 dBFS mean square
    public const int TransientHoldoffSlots = 25;           // max one onset per 25 ms per side
    public const int TransientPeakSlots = 3;               // hit strength = envelope peak over 3 ms
    public const float TransientScale = 1.41f;             // keeps the old strength (2·rms of a sine onset ≈ 1.41·peak)

    private static readonly float AttackCoeff = MathF.Exp(-1f / (0.001f * SampleRate));   // 1 ms
    private static readonly float ReleaseCoeff = MathF.Exp(-1f / (0.020f * SampleRate));  // 20 ms

    /// <summary>
    /// Haptic band limit applied to EVERY input (Native rear channels and game audio alike), so both
    /// paths are analysed identically. Flat (≥ 0.99) over the Sensa band 30–400 Hz; content above
    /// it (harmonics of hard onsets, noise textures, voices) cannot be played by the actuators and
    /// used to skew the zero-crossing frequency and onset energy. Group delay ≈ 1.5 ms.
    /// </summary>
    public const double BandLimitHz = 550;
    public const int BandLimitOrder = 8;

    private sealed class Side
    {
        public readonly ButterworthLowPass Band = new(BandLimitHz, BandLimitOrder);
        public float Env, SlotPeak, SlotEnergy, LongEnergy = 1e-6f, PrevMean;
        public bool Positive;
        public int SlotZc;
        public readonly float[] Peak = new float[RingSlots];
        public readonly float[] Energy = new float[RingSlots];
        public readonly byte[] Zc = new byte[RingSlots];
        public readonly long[] FirstCross = new long[RingSlots];
        public readonly long[] LastCross = new long[RingSlots];
        public long SlotFirstCross = -1, SlotLastCross = -1;
        public readonly bool[] Onset = new bool[RingSlots];
        public readonly float[] OnsetAmp = new float[RingSlots];
        public long LastOnsetSlot = long.MinValue / 2;

        public Side() { Array.Fill(FirstCross, -1); Array.Fill(LastCross, -1); }

        public void Reset()
        {
            Band.Reset();
            Env = SlotPeak = SlotEnergy = 0; LongEnergy = 1e-6f; PrevMean = 0; Positive = false; SlotZc = 0;
            SlotFirstCross = SlotLastCross = -1;
            Array.Clear(Peak); Array.Clear(Energy); Array.Clear(Zc); Array.Clear(Onset); Array.Clear(OnsetAmp);
            Array.Fill(FirstCross, -1); Array.Fill(LastCross, -1);
            LastOnsetSlot = long.MinValue / 2;
        }
    }

    private readonly object _gate = new();
    private readonly Side _left = new(), _right = new();
    private long _completedSlots;
    private long _sampleIndex;
    private int _samplesInSlot;
    private long _lastPushTick;
    private readonly object _spectrumGate = new();
    private MultiResolutionSpectrumAnalyzer? _spectrumAnalyzer;
    private float[]? _pcmLeft, _pcmRight, _snapshotLeft, _snapshotRight;
    private int _pcmPosition, _pcmCount;

    /// <summary>Explicit experimental opt-in; original PCM history only, FFT runs on the consumer.</summary>
    public void EnableSpectralAnalysis(bool enabled)
    {
        lock (_spectrumGate) lock (_gate) {
            if (_pcmLeft != null) Array.Clear(_pcmLeft);
            if (_pcmRight != null) Array.Clear(_pcmRight);
            if (_snapshotLeft != null) Array.Clear(_snapshotLeft);
            if (_snapshotRight != null) Array.Clear(_snapshotRight);
            _spectrumAnalyzer = enabled ? new MultiResolutionSpectrumAnalyzer() : null;
            _pcmLeft = enabled ? new float[HapticSpectrumAnalyzer.WindowSamples] : null;
            _pcmRight = enabled ? new float[HapticSpectrumAnalyzer.WindowSamples] : null;
            _snapshotLeft = enabled ? new float[HapticSpectrumAnalyzer.WindowSamples] : null;
            _snapshotRight = enabled ? new float[HapticSpectrumAnalyzer.WindowSamples] : null;
            _pcmPosition = _pcmCount = 0;
        }
    }

    private void AppendOriginal(float l, float r)
    {
        if (_pcmLeft == null) return;
        _pcmLeft[_pcmPosition] = l; _pcmRight![_pcmPosition] = r;
        _pcmPosition = (_pcmPosition + 1) % HapticSpectrumAnalyzer.WindowSamples;
        _pcmCount = Math.Min(HapticSpectrumAnalyzer.WindowSamples, _pcmCount + 1);
    }

    public bool TryTakeSpectrum(out HapticSpectrum left, out HapticSpectrum right)
    {
        lock (_spectrumGate) {
            left = right = default;
            int valid;
            lock (_gate) {
                // 42.7 ms of audio give the >= 60 Hz components; the bass joins once 85.3 ms exist.
                if (_spectrumAnalyzer == null || _pcmCount < MultiResolutionSpectrumAnalyzer.ShortSamples) return false;
                valid = _pcmCount;
                for (int i = 0; i < HapticSpectrumAnalyzer.WindowSamples; i++) {
                    int p = (_pcmPosition + i) % HapticSpectrumAnalyzer.WindowSamples;
                    _snapshotLeft![i] = _pcmLeft![p]; _snapshotRight![i] = _pcmRight![p];
                }
            }
            // No FFT, JSON or vendor call while holding the capture/feature lock.
            left = _spectrumAnalyzer.Analyze(_snapshotLeft, valid); right = _spectrumAnalyzer.Analyze(_snapshotRight, valid);
            return true;
        }
    }

    /// <summary>Monotonic count of completed 1 ms slots (for tests/diagnostics).</summary>
    public long CompletedSlots { get { lock (_gate) return _completedSlots; } }
    public long LastPushTick => Interlocked.Read(ref _lastPushTick);

    public void Reset()
    {
        lock (_gate)
        {
            _left.Reset(); _right.Reset();
            _completedSlots = 0; _samplesInSlot = 0; _sampleIndex = 0;
            _pcmPosition = _pcmCount = 0;
        }
    }

    /// <summary>Clear pending/ringing source features without moving the consumer clock backwards.</summary>
    public void ClearForSilence()
    {
        lock (_gate)
        {
            _left.Reset(); _right.Reset();
            _samplesInSlot = 0;
            _pcmPosition = _pcmCount = 0;
            _sampleIndex = _completedSlots * SlotSamples;
            CloseSlot(); // A newly published all-zero slot prevents holding the previous sound.
        }
    }

    /// <summary>Feeds interleaved 4-channel frames (PCM16 or Float32); channels 2/3 are left/right.</summary>
    public void Push(ReadOnlySpan<byte> fourChannel, FourChannelHapticAnalyzer.PcmEncoding encoding)
    {
        int frameBytes = encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16 ? 8 : 16;
        int frames = fourChannel.Length / frameBytes;
        if (frames == 0) return;
        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                var frame = fourChannel.Slice(f * frameBytes, frameBytes);
                float l, r;
                if (encoding == FourChannelHapticAnalyzer.PcmEncoding.Pcm16)
                {
                    l = (short)(frame[4] | (frame[5] << 8)) / 32768f;
                    r = (short)(frame[6] | (frame[7] << 8)) / 32768f;
                }
                else
                {
                    l = BitConverter.ToSingle(frame.Slice(8, 4));
                    r = BitConverter.ToSingle(frame.Slice(12, 4));
                    if (!float.IsFinite(l)) l = 0;
                    if (!float.IsFinite(r)) r = 0;
                }
                AppendOriginal(l, r);
                Process(_left, l, _sampleIndex);
                Process(_right, r, _sampleIndex);
                _sampleIndex++;
                if (++_samplesInSlot == SlotSamples) CloseSlot();
            }
        }
        Interlocked.Exchange(ref _lastPushTick, Environment.TickCount64);
    }

    /// <summary>
    /// Feeds interleaved stereo float samples at 48 kHz (e.g. filtered game audio for the
    /// PC-mode "Sesten titreşim" path). Same analysis as the DualSense rear channels.
    /// </summary>
    public void PushStereo(ReadOnlySpan<float> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        if (frames == 0) return;
        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                float l = interleavedStereo[2 * f], r = interleavedStereo[2 * f + 1];
                if (!float.IsFinite(l)) l = 0;
                if (!float.IsFinite(r)) r = 0;
                AppendOriginal(l, r);
                Process(_left, l, _sampleIndex);
                Process(_right, r, _sampleIndex);
                _sampleIndex++;
                if (++_samplesInSlot == SlotSamples) CloseSlot();
            }
        }
        Interlocked.Exchange(ref _lastPushTick, Environment.TickCount64);
    }

    private static void Process(Side s, float x, long sample)
    {
        x = s.Band.Process(x);
        float ax = MathF.Abs(x);
        s.Env = ax > s.Env ? ax + (s.Env - ax) * AttackCoeff : ax + (s.Env - ax) * ReleaseCoeff;
        if (s.Env > s.SlotPeak) s.SlotPeak = s.Env;
        s.SlotEnergy += x * x;
        float hyst = Math.Max(ZeroCrossThreshold, ZeroCrossRelative * s.Env);
        if (!s.Positive && x > hyst)
        {
            s.Positive = true; s.SlotZc++;
            if (s.SlotFirstCross < 0) s.SlotFirstCross = sample;
            s.SlotLastCross = sample;
        }
        else if (s.Positive && x < -hyst) s.Positive = false;
    }

    private void CloseSlot()
    {
        int idx = (int)(_completedSlots % RingSlots);
        CloseSide(_left, idx, _completedSlots);
        CloseSide(_right, idx, _completedSlots);
        _completedSlots++;
        _samplesInSlot = 0;
    }

    private static void CloseSide(Side s, int idx, long slot)
    {
        float mean = s.SlotEnergy / SlotSamples;
        s.Peak[idx] = Math.Min(1f, s.SlotPeak);
        s.Energy[idx] = mean;
        s.Zc[idx] = (byte)Math.Min(255, s.SlotZc);
        s.FirstCross[idx] = s.SlotFirstCross; s.LastCross[idx] = s.SlotLastCross;
        s.SlotFirstCross = s.SlotLastCross = -1;
        // Onset test on a 2 ms sliding energy: a hit straddling a 1 ms slot boundary is caught the
        // same way wherever it lands (2026-11-08: single-slot energy made the same signal shifted
        // by <= 0.5 ms differ in transient count and strength by up to 0.70).
        float e2 = 0.5f * (mean + s.PrevMean);
        bool onset = Math.Max(mean, e2) > TransientFloor && Math.Max(mean, e2) > s.LongEnergy * TransientRatio &&
                     slot - s.LastOnsetSlot >= TransientHoldoffSlots;
        s.Onset[idx] = onset;
        s.OnsetAmp[idx] = 0;                                   // set in Build from the envelope peak
        if (onset) s.LastOnsetSlot = slot;
        s.PrevMean = mean;
        s.LongEnergy = s.LongEnergy * 0.96f + mean * 0.04f;   // ≈ 25 ms running mean
        s.SlotPeak = 0; s.SlotEnergy = 0; s.SlotZc = 0;
    }

    /// <summary>
    /// Returns features for the slots completed since <paramref name="cursor"/> and advances it.
    /// Returns the number of new slots (0 = nothing new; the caller should hold its last output).
    /// </summary>
    public int Take(ref long cursor, out SideFeatures left, out SideFeatures right)
    {
        lock (_gate)
        {
            long end = _completedSlots;
            long start = Math.Max(cursor, end - RingSlots + 1);
            int count = (int)Math.Max(0, end - start);
            left = Build(_left, start, end, count);
            right = Build(_right, start, end, count);
            cursor = end;
            return count;
        }
    }

    private static SideFeatures Build(Side s, long start, long end, int count)
    {
        var f = new SideFeatures();
        if (count <= 0) return f;
        // 4 points: max envelope of each quarter of the new window.
        for (int q = 0; q < 4; q++)
        {
            long a = start + (long)q * count / 4, b = start + (long)(q + 1) * count / 4;
            if (b <= a) b = a + 1;
            float m = 0;
            for (long i = a; i < b && i < end; i++) m = Math.Max(m, s.Peak[(int)(i % RingSlots)]);
            switch (q) { case 0: f.P0 = m; break; case 1: f.P1 = m; break; case 2: f.P2 = m; break; default: f.P3 = m; break; }
        }
        // Frequency: exact period between the first and last rising crossing in the last 40 ms.
        int zc = 0; float energy = 0; long first = -1, last = -1;
        long fs = Math.Max(0, end - FrequencyWindowSlots);
        for (long i = fs; i < end; i++)
        {
            int k = (int)(i % RingSlots);
            zc += s.Zc[k]; energy += s.Energy[k];
            if (s.FirstCross[k] >= 0 && (first < 0 || s.FirstCross[k] < first)) first = s.FirstCross[k];
            if (s.LastCross[k] > last) last = s.LastCross[k];
        }
        int span = (int)(end - fs);
        f.FrequencyHz = span > 0 && energy / span > 1e-6f && zc >= 2 && last > first
            ? (zc - 1) * (float)SampleRate / (last - first) : 0f;
        // Up to two onsets inside the new window.
        for (long i = start; i < end && f.TransientCount < 2; i++)
        {
            int k = (int)(i % RingSlots);
            if (!s.Onset[k]) continue;
            float pos = count > 1 ? (float)(i - start) / (count - 1) : 0f;
            // Strength = envelope peak over the hit's first TransientPeakSlots ms (alignment independent).
            float amp = 0;
            for (long j = i; j < Math.Min(end, i + TransientPeakSlots); j++) amp = Math.Max(amp, s.Peak[(int)(j % RingSlots)]);
            amp = Math.Min(1f, amp * TransientScale);
            if (f.TransientCount == 0) { f.T0Position = pos; f.T0Amplitude = amp; }
            else { f.T1Position = pos; f.T1Amplitude = amp; }
            f.TransientCount++;
        }
        return f;
    }
}
