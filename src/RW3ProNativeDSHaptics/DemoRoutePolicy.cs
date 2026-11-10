using System;

namespace RW3ProNativeDSHaptics;

/// <summary>Where a Wolverine demo goes. Before 2026-11-09 demos only knew Sensa, so in Xbox mode they
/// failed ("Wolverine PC modunda bulunamadı"), and starting one stopped a running virtual bridge.</summary>
public enum DemoRoute
{
    /// <summary>Virtual bridge running: play the demo into it like a game (PC: Sensa, Xbox: body motors).</summary>
    VirtualBridge,
    /// <summary>PC mode, no bridge: the Sensa engine directly.</summary>
    Sensa,
    /// <summary>Xbox mode, no bridge: body motors from the haptic channels (left ← ch3, right ← ch4).</summary>
    XboxBody,
    NoController
}

public static class DemoRoutePolicy
{
    /// <param name="xboxSensa">Xbox mode can reach the palms through Sensa over GIP (setting XboxSensa).</param>
    public static DemoRoute Choose(bool bridgeRunning, WolverineControllerMode mode, bool xboxSensa = false) =>
        bridgeRunning ? DemoRoute.VirtualBridge
        : mode == WolverineControllerMode.Pc ? DemoRoute.Sensa
        : mode == WolverineControllerMode.Xbox ? (xboxSensa ? DemoRoute.Sensa : DemoRoute.XboxBody)
        : DemoRoute.NoController;

    public static string Describe(DemoRoute route) => route switch
    {
        DemoRoute.VirtualBridge => "sanal köprü üzerinden (oyun gibi)",
        DemoRoute.Sensa => "Sensa avuçlar",
        DemoRoute.XboxBody => "gövde motorları (Xbox modu)",
        _ => "kol bulunamadı"
    };

    /// <summary>Haptic channels 3/4 of interleaved 4-channel PCM16 as floats.</summary>
    public static (float[] L, float[] R) HapticChannels(byte[] quadPcm16)
    {
        int frames = quadPcm16.Length / 8;
        var l = new float[frames]; var r = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            l[i] = BitConverter.ToInt16(quadPcm16, i * 8 + 4) / 32768f;
            r[i] = BitConverter.ToInt16(quadPcm16, i * 8 + 6) / 32768f;
        }
        return (l, r);
    }
}
