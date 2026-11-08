using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RW3ProNativeDSHaptics;

public readonly record struct AcousticBin(double SampleTimeMs, double BandRms, double RawPeak);
public sealed record AcousticWindow(long Samples, double BandRms, double Peak, long Clipped,
    IReadOnlyList<AcousticBin>? Bins = null, double? ProbeFrequencyHz = null,
    double? ProbeToneAmplitude = null, int TruncatedBins = 0, AcousticToneBand[]? ToneBands = null);

/// <summary>Opt-in microphone meter. Retains aggregate levels only, no voice/PCM recording or upload.</summary>
public sealed class MicrophoneLevelProbe : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly bool _floatInput;
    private readonly object _gate = new();
    private long _samples, _clipped;
    private double _squares, _peak, _previousInput, _highPass, _lowPass;
    private readonly double _hpAlpha, _lpAlpha;
    private readonly int _sampleRate, _binFrames;
    private readonly AcousticBin[] _bins = new AcousticBin[1000]; // At most 10 seconds of levels, never raw audio.
    private int _binSamples, _binCount, _truncatedBins;
    private double _binSquares, _binPeak, _probeHz, _goertzelCoefficient, _s1, _s2;
    public string? Failure { get; private set; }
    public string DeviceName { get; }
    private readonly AcousticToneBands? _toneBands;

    public MicrophoneLevelProbe(string endpointId, bool useWaveIn = false, bool collectToneBands = false)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        DeviceName = device.FriendlyName;
        if (device.AudioEndpointVolume.Mute)
            throw new InvalidOperationException("Seçili mikrofon sessizde. Ölçüm yapılmadı; mikrofonun sessizliğini kaldır.");
        if (useWaveIn)
        {
            var candidates = new List<int>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                if (DeviceName.StartsWith(WaveIn.GetCapabilities(i).ProductName, StringComparison.OrdinalIgnoreCase)) candidates.Add(i);
            if (candidates.Count != 1) throw new InvalidOperationException("Legacy microphone must match exactly one endpoint by its Windows name.");
            _capture = new WaveInEvent { DeviceNumber = candidates[0], WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 20 };
        }
        else _capture = new WasapiCapture(device);
        var format = _capture.WaveFormat;
        _sampleRate = format.SampleRate;
        _toneBands = collectToneBands ? new AcousticToneBands(_sampleRate) : null;
        _binFrames = Math.Max(1, format.SampleRate / 100);
        _floatInput = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat ||
              format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        if (!_floatInput && !(useWaveIn && format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
        {
            _capture.Dispose();
            throw new NotSupportedException("Mikrofon ölçümü Float32 WASAPI biçimi gerektirir.");
        }
        _hpAlpha = Math.Exp(-2 * Math.PI * 30 / format.SampleRate);
        _lpAlpha = 1 - Math.Exp(-2 * Math.PI * 600 / format.SampleRate);
        _capture.DataAvailable += OnAudio;
        _capture.RecordingStopped += (_, args) => Failure = args.Exception?.Message ?? "Mikrofon yakalama durdu.";
        try { _capture.StartRecording(); }
        catch { _capture.Dispose(); throw; }
    }

    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        int frameBytes = _capture.WaveFormat.BlockAlign;
        lock (_gate)
        {
            for (int i = 0; i + frameBytes <= args.BytesRecorded; i += frameBytes)
            {
                // Meter the first mic channel; stereo cancellation cannot hide vibration.
                float input = _floatInput ? BitConverter.ToSingle(args.Buffer, i) : BitConverter.ToInt16(args.Buffer, i) / 32768f;
                if (!float.IsFinite(input)) continue;
                _toneBands?.Push(input);
                if (Math.Abs(input) >= .999f) ++_clipped;
                _highPass = _hpAlpha * (_highPass + input - _previousInput);
                _previousInput = input;
                _lowPass += _lpAlpha * (_highPass - _lowPass);
                _squares += _lowPass * _lowPass;
                _binSquares += _lowPass * _lowPass;
                _binPeak = Math.Max(_binPeak, Math.Abs(input));
                if (_probeHz > 0)
                {
                    double next = input + _goertzelCoefficient * _s1 - _s2;
                    _s2 = _s1; _s1 = next;
                }
                _peak = Math.Max(_peak, Math.Abs(input));
                ++_samples;
                if (++_binSamples == _binFrames)
                {
                    if (_binCount < _bins.Length)
                        _bins[_binCount++] = new(_samples * 1000.0 / _sampleRate, Math.Sqrt(_binSquares / _binSamples), _binPeak);
                    else _truncatedBins++;
                    _binSamples = 0; _binSquares = _binPeak = 0;
                }
            }
        }
    }

    public void BeginWindow(double probeFrequencyHz = 0)
    {
        if (!double.IsFinite(probeFrequencyHz) || probeFrequencyHz < 0 || probeFrequencyHz >= _sampleRate / 2)
            throw new ArgumentOutOfRangeException(nameof(probeFrequencyHz));
        lock (_gate)
        {
            _samples = 0; _squares = 0; _peak = 0; _clipped = 0;
            Array.Clear(_bins); _binSamples = _binCount = _truncatedBins = 0; _binSquares = _binPeak = 0;
            _probeHz = probeFrequencyHz; _goertzelCoefficient = 2 * Math.Cos(2 * Math.PI * _probeHz / _sampleRate);
            _s1 = _s2 = 0;
            _toneBands?.Reset();
        }
    }
    public AcousticWindow ReadWindow()
    {
        lock (_gate)
        {
            var points = new AcousticBin[_binCount];
            Array.Copy(_bins, points, _binCount);
            double tone = _samples > 0 ? 2 * Math.Sqrt(Math.Max(0, _s1 * _s1 + _s2 * _s2 - _goertzelCoefficient * _s1 * _s2)) / _samples : 0;
            return new(_samples, _samples > 0 ? Math.Sqrt(_squares / _samples) : 0, _peak, _clipped,
                points, _probeHz > 0 ? _probeHz : null, _probeHz > 0 ? tone : null, _truncatedBins, _toneBands?.Read());
        }
    }
    public void Dispose()
    {
        _capture.DataAvailable -= OnAudio;
        try { _capture.StopRecording(); } finally { _capture.Dispose(); }
    }
}
