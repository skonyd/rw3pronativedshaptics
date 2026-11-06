using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;
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

        // The ONLY scene generation is before the split. The USB reference is
        // passed directly to RawSourceWaveStream, with no gain processing.
        byte[] rawReference = BehavioralHapticFixtures.Generate(scene);
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
            Task usbTask = usb ? PlayUsbReferenceAsync(usbEndpointId!, rawReference,
                exclusiveUsb, linked.Token) : Task.CompletedTask;
            Task razerTask = razer ? PlayRazerEnvelopeAsync(rawReference,
                wolverineTestLevelPercent, sensa, linked.Token) : Task.CompletedTask;
            // Near-simultaneous async starts. WASAPI and vendor scheduling cannot
            // guarantee sample-locked onset between physically different devices.
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

    private static async Task PlayUsbReferenceAsync(
        string endpointId, byte[] unmodifiedPcm, bool exclusive, CancellationToken cancel)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        if (device.State != DeviceState.Active)
            throw new InvalidOperationException("Seçilen USB DualSense aygıtı bağlı/aktif değil.");
        var mix = device.AudioClient.MixFormat;
        if (mix.Channels != 4 || mix.SampleRate != BehavioralHapticFixtures.SampleRate)
            throw new InvalidOperationException("Gerçek DualSense 4-kanal/48kHz formatı gerekli.");
        var format = new WaveFormat(BehavioralHapticFixtures.SampleRate, 16, 4);
        var mode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        if (exclusive && !device.AudioClient.IsFormatSupported(mode, format))
            throw new NotSupportedException(
                "Exclusive 4ch/48k PCM16 aygıt tarafından desteklenmiyor. " +
                "Referans veriyi dönüştürmeden korumak için sessiz fallback yapılmaz. " +
                "İstersen Windows Shared modunu açıkça seç.");
        using var memory = new MemoryStream(unmodifiedPcm, writable: false);
        using var provider = new RawSourceWaveStream(memory, format);
        using var output = new WasapiOut(device, mode, true, 50);
        output.Init(provider);
        try
        {
            cancel.ThrowIfCancellationRequested();
            output.Play();
            int lengthMs = unmodifiedPcm.Length * 1000 /
                (BehavioralHapticFixtures.SampleRate * BehavioralHapticFixtures.BytesPerFrame);
            await Task.Delay(lengthMs + 130, cancel);
        }
        finally { output.Stop(); }
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
