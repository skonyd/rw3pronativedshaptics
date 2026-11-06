using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

public enum ComparisonTarget { Both, DualSenseOnly, WolverineOnly }

public sealed record ComparisonResult(
    HapticBehavior Scene, ComparisonTarget Target,
    bool UsbPcmSubmitted, bool RazerProviderTicked, bool ExclusiveUsbMode);

/// <summary>
/// The 4ch/48k PCM16 source buffer is passed UNCHANGED to the physical USB
/// DualSense audio endpoint (ch0/1 silence; ch2/3 haptics). The program does NOT
/// EQ/scale/normalize/mix the reference. Shared-mode Windows driver/APO conversion
/// is outside this app; users can explicitly select strict exclusive PCM if supported.
/// Wolverine receives a separate LOSSY 50 Hz envelope reconstruction.
/// </summary>
public static class BehavioralComparisonPlayer
{
    public const int FrameStepMs = 20;

    public static async Task<ComparisonResult> PlayAsync(
        HapticBehavior scene, ComparisonTarget target,
        string? usbEndpointId, bool exclusiveUsb,
        int wolverineTestLevelPercent, SensaStereoPrototype sensa,
        CancellationToken cancel)
    {
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        if (wolverineTestLevelPercent < 0 || wolverineTestLevelPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(wolverineTestLevelPercent));
        bool usb = target is ComparisonTarget.Both or ComparisonTarget.DualSenseOnly;
        bool razer = target is ComparisonTarget.Both or ComparisonTarget.WolverineOnly;
        if (usb && string.IsNullOrWhiteSpace(usbEndpointId))
            throw new InvalidOperationException("Gerçek USB DualSense 4ch/48k aygıtını seç.");
        cancel.ThrowIfCancellationRequested();

        // One deterministic source. USB receives the same unmodified PCM byte[].
        byte[] rawReference = BehavioralHapticFixtures.Generate(scene);
        // Reserve the physical USB output BEFORE energizing either controller.
        // A failing exclusive-format check must not produce a one-sided A/B run.
        using var preparedUsb = usb
            ? new PreparedUsbPlayback(usbEndpointId!, rawReference, exclusiveUsb)
            : null;
        cancel.ThrowIfCancellationRequested();

        int before = sensa.EngineTicks;
        if (razer)
        {
            if (!sensa.IsActive && !sensa.Start())
                throw new InvalidOperationException(sensa.Status);
            for (int i = 0; i < 30 && !sensa.IsActive && sensa.IsRunning; i++)
                await Task.Delay(80, cancel);
            if (!sensa.IsActive)
                throw new InvalidOperationException("Razer provider aktif değil: " + sensa.Status);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            cancel.ThrowIfCancellationRequested();
            // The start calls are close in time, but motor output is not
            // guaranteed sample-aligned across independent device clocks.
            Task usbTask = usb ? preparedUsb!.PlayAsync(linked.Token) : Task.CompletedTask;
            Task razerTask = razer ? PlayRazerEnvelopeAsync(rawReference,
                wolverineTestLevelPercent, sensa, linked.Token) : Task.CompletedTask;
            try { await Task.WhenAll(usbTask, razerTask); }
            catch
            {
                linked.Cancel();
                try { await Task.WhenAll(usbTask, razerTask); } catch { }
                throw;
            }
            return new ComparisonResult(scene, target, usb,
                razer && sensa.EngineTicks > before, exclusiveUsb && usb);
        }
        finally
        {
            sensa.Submit(0, 0);
        }
    }

    // Prepares WASAPI and validates the selected endpoint before playback begins.
    // A/R failures no longer leave the USB DualSense playing by itself.
    private sealed class PreparedUsbPlayback : IDisposable
    {
        private readonly List<IDisposable> _resources = new();
        private WasapiOut? _output;
        private readonly int _durationMs;

        public PreparedUsbPlayback(string endpointId, byte[] unchangedPcm, bool exclusive)
        {
            try
            {
                var enumerator = Own(new MMDeviceEnumerator());
                var device = Own(enumerator.GetDevice(endpointId));
                if (device.State != DeviceState.Active)
                    throw new InvalidOperationException("Seçilen USB DualSense aktif değil.");
                var mix = device.AudioClient.MixFormat;
                if (mix.Channels != 4 || mix.SampleRate != BehavioralHapticFixtures.SampleRate)
                    throw new InvalidOperationException("USB DualSense için 4ch/48kHz aygıt gerekli.");

                var format = new WaveFormat(BehavioralHapticFixtures.SampleRate, 16, 4);
                var mode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
                if (exclusive && !device.AudioClient.IsFormatSupported(mode, format))
                    throw new NotSupportedException(
                        "Exclusive 4ch/48k PCM16 bu aygıtta desteklenmiyor. " +
                        "RW3 otomatik Shared moda geçmez; kullanıcı açıkça seçmelidir.");
                var memory = Own(new MemoryStream(unchangedPcm, writable: false));
                var source = Own(new RawSourceWaveStream(memory, format));
                _output = Own(new WasapiOut(device, mode, true, 50));
                _output.Init(source);
                _durationMs = unchangedPcm.Length * 1000 /
                    (BehavioralHapticFixtures.SampleRate *
                     BehavioralHapticFixtures.BytesPerFrame);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private T Own<T>(T resource) where T : IDisposable
        {
            _resources.Add(resource);
            return resource;
        }

        public async Task PlayAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var output = _output ?? throw new ObjectDisposedException(nameof(PreparedUsbPlayback));
            try
            {
                output.Play();
                // A WASAPI Play() call only proves dispatch; no actuator
                // measurement or guaranteed full duration is implied.
                await Task.Delay(_durationMs + 130, token);
            }
            finally
            {
                output.Stop();
            }
        }

        public void Dispose()
        {
            for (int i = _resources.Count - 1; i >= 0; --i)
            {
                try { _resources[i].Dispose(); }
                catch (Exception ex) { DiagnosticsService.Error("USB A/B output dispose", ex); }
            }
            _resources.Clear();
            _output = null;
        }
    }

    private static async Task PlayRazerEnvelopeAsync(
        byte[] unmodifiedPcm, int testLevelPercent,
        SensaStereoPrototype sensa, CancellationToken cancel)
    {
        int frames = unmodifiedPcm.Length / BehavioralHapticFixtures.BytesPerFrame;
        int framesPerBlock = BehavioralHapticFixtures.SampleRate * FrameStepMs / 1000;
        float factor = testLevelPercent / 100f;
        var timer = Stopwatch.StartNew();
        try
        {
            for (int first = 0, index = 0; first < frames; first += framesPerBlock, index++)
            {
                cancel.ThrowIfCancellationRequested();
                if (!sensa.IsActive)
                    throw new InvalidOperationException("Wolverine Razer provider bağlantısı kayboldu.");
                var levels = BehavioralHapticFixtures.RmsWindow(
                    unmodifiedPcm, first, Math.Min(framesPerBlock, frames - first));
                sensa.Submit(Math.Min(0.32f, levels.Left * 2.4f * factor),
                             Math.Min(0.32f, levels.Right * 2.4f * factor));
                int delay = (index + 1) * FrameStepMs - (int)timer.ElapsedMilliseconds;
                if (delay > 0) await Task.Delay(delay, cancel);
            }
        }
        finally { sensa.Submit(0, 0); }
    }
}
