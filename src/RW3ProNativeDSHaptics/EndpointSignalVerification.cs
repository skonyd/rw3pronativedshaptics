using System;

namespace RW3ProNativeDSHaptics;

public sealed record EndpointSampleComparison(bool SourceHasRearEnergy, bool CompleteActiveRegion,
    bool ExactActiveSamples, double Correlation, double ObservedGain, double RawRmsError,
    int AlignmentFrames, int ComparedFrames, int CapturedFrames, float CapturedLeftPeak, float CapturedRightPeak);

/// <summary>Offline comparison only. Reports OS gain/errors without correcting either source or capture.</summary>
public static class EndpointSignalVerification
{
    public static EndpointSampleComparison Compare(byte[] originalQuad16, byte[] capturedStereoWave, HapticPcmFormat format)
    {
        int header = format == HapticPcmFormat.Signed16 ? 44 : 56;
        int bytes = HapticStereoWave.BytesPerSample(format), stride = bytes * 2;
        if (originalQuad16.Length == 0 || originalQuad16.Length % 8 != 0 || capturedStereoWave.Length <= header ||
            (capturedStereoWave.Length - header) % stride != 0)
            throw new ArgumentException("Complete bounded source/capture frames required.");
        int expectedFrames = originalQuad16.Length / 8, capturedFrames = (capturedStereoWave.Length - header) / stride;
        if (expectedFrames > 480000 || capturedFrames > 480000) throw new ArgumentException("Comparison limited to 10 seconds.");
        float Expected(int frame, int side) => BitConverter.ToInt16(originalQuad16, frame * 8 + 4 + side * 2) / 32768f;
        float Captured(int frame, int side)
        {
            int offset = header + frame * stride + side * bytes;
            float value = format == HapticPcmFormat.Signed16 ? BitConverter.ToInt16(capturedStereoWave, offset) / 32768f :
                BitConverter.ToSingle(capturedStereoWave, offset);
            if (!float.IsFinite(value)) throw new ArgumentException("Nonfinite capture sample; fidelity comparison invalid.");
            return value;
        }
        float expectedPeak = 0, lp = 0, rp = 0;
        double capturedSquares = 0;
        for (int i = 0; i < expectedFrames; i++) expectedPeak = Math.Max(expectedPeak, Math.Max(Math.Abs(Expected(i, 0)), Math.Abs(Expected(i, 1))));
        for (int i = 0; i < capturedFrames; i++)
        {
            float l = Captured(i, 0), r = Captured(i, 1);
            lp = Math.Max(lp, Math.Abs(l)); rp = Math.Max(rp, Math.Abs(r));
            capturedSquares += (double)l * l + (double)r * r;
        }
        if (expectedPeak == 0)
            return new(false, capturedFrames >= expectedFrames, lp == 0 && rp == 0, 0, 0,
                Math.Sqrt(capturedSquares / (capturedFrames * 2)), 0, 0, capturedFrames, lp, rp);
        int firstExpected = 0, lastExpected = expectedFrames - 1, firstCapture = 0;
        float thresholdExpected = expectedPeak * .005f, thresholdCapture = Math.Max(lp, rp) * .005f;
        while (firstExpected < expectedFrames && Math.Max(Math.Abs(Expected(firstExpected, 0)), Math.Abs(Expected(firstExpected, 1))) <= thresholdExpected) firstExpected++;
        while (lastExpected >= firstExpected && Math.Max(Math.Abs(Expected(lastExpected, 0)), Math.Abs(Expected(lastExpected, 1))) <= thresholdExpected) lastExpected--;
        while (firstCapture < capturedFrames && Math.Max(Math.Abs(Captured(firstCapture, 0)), Math.Abs(Captured(firstCapture, 1))) <= thresholdCapture) firstCapture++;
        int candidate = firstCapture - firstExpected, best = candidate;
        double bestCorrelation = -1;
        for (int shift = candidate - 256; shift <= candidate + 256; shift++)
        {
            double dot = 0, e = 0, c = 0;
            int end = Math.Min(lastExpected + 1, firstExpected + 48000);
            for (int i = firstExpected; i < end; i += 16)
            {
                if (i + shift < 0 || i + shift >= capturedFrames) continue;
                for (int side = 0; side < 2; side++)
                {
                    double x = Expected(i, side), y = Captured(i + shift, side);
                    dot += x * y; e += x * x; c += y * y;
                }
            }
            double correlation = e > 0 && c > 0 ? dot / Math.Sqrt(e * c) : 0;
            if (correlation > bestCorrelation) { bestCorrelation = correlation; best = shift; }
        }
        double sumDot = 0, sumE = 0, sumC = 0, error = 0;
        int count = 0;
        bool exact = true, complete = firstExpected + best >= 0 && lastExpected + best < capturedFrames;
        for (int i = firstExpected; i <= lastExpected; i++)
        {
            if (i + best < 0 || i + best >= capturedFrames) { exact = false; continue; }
            for (int side = 0; side < 2; side++)
            {
                double x = Expected(i, side), y = Captured(i + best, side);
                sumDot += x * y; sumE += x * x; sumC += y * y; error += (x - y) * (x - y);
                exact &= x == y;
            }
            count++;
        }
        return new(true, complete, exact && complete, sumE > 0 && sumC > 0 ? sumDot / Math.Sqrt(sumE * sumC) : 0,
            sumE > 0 ? sumDot / sumE : 0, count > 0 ? Math.Sqrt(error / (count * 2)) : 0,
            best, count, capturedFrames, lp, rp);
    }
}
