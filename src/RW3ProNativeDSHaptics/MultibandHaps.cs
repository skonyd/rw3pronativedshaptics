using System;
using System.Text.Json;

namespace RW3ProNativeDSHaptics;

/// <summary>Project-authored v5 .haps via documented public schema. No frame packing, no phase command.</summary>
public static class MultibandHaps
{
    public static string Build(HapticSpectrum spectrum, SideFeatures envelope, double gain, out bool headroomReduced)
    {
        if (!double.IsFinite(gain) || gain < 0) throw new ArgumentOutOfRangeException(nameof(gain));
        if (spectrum.Count is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(spectrum));
        for (int i = 0; i < spectrum.Count; i++) {
            var c = spectrum[i];
            if (!float.IsFinite(c.FrequencyHz) || c.FrequencyHz is < 30 or > 400 ||
                !float.IsFinite(c.Amplitude) || c.Amplitude < 0) throw new ArgumentException("Invalid spectral component.", nameof(spectrum));
        }
        if (!float.IsFinite(envelope.Peak)) throw new ArgumentException("Invalid envelope.", nameof(envelope));
        double sum = spectrum.AmplitudeSum * gain;
        headroomReduced = sum > 1;
        double scale = sum > 1 ? gain / sum : gain;
        object Curve(double[] values, bool shortAmplitude = false) => new { m_extrapolationStrategy = 0,
            m_keyframes = new[] { new { m_time = 0d, m_value = values[0] }, new { m_time = .011, m_value = values[1] },
                new { m_time = .022, m_value = values[2] }, new { m_time = .033, m_value = values[3] },
                new { m_time = .063, m_value = values[3] }, new { m_time = .066, m_value = shortAmplitude ? 0 : values[3] },
                new { m_time = 2d, m_value = shortAmplitude ? 0 : values[3] } } };
        object Note(HapticSpectralComponent component) {
            double amplitude = component.Amplitude * scale;
            double Ratio(float value) => envelope.Peak > 1e-6 ? Math.Clamp(value / envelope.Peak, 0, 1) : 0;
            return new { m_gain = 1d, m_startingPoint = 0d, m_length = 2d, m_priority = 1,
                m_hapticEffect = new { m_amplitudeModulation = Curve([amplitude * Ratio(envelope.P0), amplitude * Ratio(envelope.P1), amplitude * Ratio(envelope.P2), amplitude * Ratio(envelope.P3)], true),
                    m_frequencyModulation = Curve([component.FrequencyHz, component.FrequencyHz, component.FrequencyHz, component.FrequencyHz]) } };
        }
        var melodies = new object[1 + Math.Max(1, spectrum.Count)];
        melodies[0] = new { m_gain = 1d, m_mute = 0, m_notes = Array.Empty<object>() };
        for (int i = 1; i < melodies.Length; i++) {
            var component = spectrum.Count > 0 ? spectrum[i - 1] : new HapticSpectralComponent(80, 0, 0);
            melodies[i] = new { m_gain = 1d, m_mute = 0, m_notes = new[] { Note(component) } };
        }
        object Perception(object[] m) => new { m_gain = 1d, m_loop = 0, m_maximum = 1d, m_signalEvaluationMethod = 3, m_melodies = m };
        return JsonSerializer.Serialize(new { m_HDFlag = 0, m_description = "RW3 experimental spectral mapping; phase/transient loss remains",
            m_gain = 1d, m_length_unit = 7, m_time_unit = 0, m_version = "5",
            m_stiffness = Perception([]), m_texture = Perception([]), m_vibration = Perception(melodies) });
    }
}
