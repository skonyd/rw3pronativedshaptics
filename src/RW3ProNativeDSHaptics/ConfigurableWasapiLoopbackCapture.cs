using NAudio.CoreAudioApi;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Version-2 NAudio capture-compatible loopback with configurable *requested*
/// buffer size. The legacy WasapiLoopbackCapture(MMDevice) constructor defaults
/// to a 100ms buffer and polling. This subclass uses its documented
/// WasapiCapture(MMDevice, useEventSync:false, milliseconds) constructor and
/// loopback stream flag. Polling remains intentional: WASAPI loopback
/// EventCallback may not notify reliably on every render endpoint.
/// No promise of IAudioClient3 minimum engine period or physical latency.
/// </summary>
public sealed class ConfigurableWasapiLoopbackCapture : WasapiCapture
{
    public int RequestedBufferMs { get; }

    public ConfigurableWasapiLoopbackCapture(MMDevice renderDevice, int requestedBufferMs)
        : base(renderDevice, false, WasapiLoopbackBufferPolicy.Validate(requestedBufferMs))
    {
        RequestedBufferMs = requestedBufferMs;
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}
