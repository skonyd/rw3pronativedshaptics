using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Explicit WASAPI loopback polling/buffer requests. Loopback is not
/// IAudioClient3 low-latency shared capture, so these are requests and not
/// guaranteed game-to-device latency values.
/// </summary>
public static class WasapiLoopbackBufferPolicy
{
    public const int DefaultRequestMs = 20;
    public static int Validate(int value) => value switch
    {
        10 or 20 or 50 or 100 => value,
        _ => throw new ArgumentOutOfRangeException(nameof(value),
            "WASAPI loopback buffer must be explicitly selected as 10, 20, 50 or 100ms.")
    };
}
