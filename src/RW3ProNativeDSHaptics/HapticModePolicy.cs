namespace RW3ProNativeDSHaptics;

/// <summary>What the app is currently running for the selected mode.</summary>
public enum RunState { Off, Audio, Native }

/// <summary>Where "Oyunun DualSense titreşimi" takes the game's DualSense output from.</summary>
public enum NativeSourcePlan
{
    /// <summary>A USB DualSense's 4-channel endpoint is present: capture it (loopback).</summary>
    UsbDualSense,
    /// <summary>Virtual DualSense bridge, started in this (elevated) process.</summary>
    VirtualHere,
    /// <summary>Virtual DualSense bridge through the UAC-free scheduled task (this process exits).</summary>
    VirtualViaTask,
    /// <summary>HIDMaestro is not installed: point the user to Settings.</summary>
    WaitForInstall,
    /// <summary>Needs one click (and a UAC prompt) - no automatic retries.</summary>
    WaitForClick
}

/// <summary>
/// The mode decisions, free of UI and devices so the contract tests cover them. Used by
/// <see cref="HapticOrchestrator"/>; previously spread through MainWindow.
/// </summary>
public static class HapticModePolicy
{
    public const long RetryMs = 5000;
    public const long CpuBackoffMs = 60_000;
    /// <summary>"Do not retry until the user changes something."</summary>
    public const long Never = long.MaxValue;

    /// <summary>May the selected mode be (re)started now? Native starts when any game feedback is wanted (haptics from
    /// the game or from sound, trigger effects or rumble): before 2026-11-14 it needed the HD PCM source, so the
    /// common "triggers, no HD haptics" setup never started the bridge by itself.</summary>
    public static bool MayAutoStart(HapticMode mode, RunState run, bool starting, bool busy, bool exiting,
        long now, long nextAutoStart, bool audioOutputEnabled, bool nativeAnySource) =>
        !busy && !starting && !exiting && run == RunState.Off
        && mode != HapticMode.Normal
        && now >= nextAutoStart
        && (mode != HapticMode.Audio || audioOutputEnabled)
        && (mode != HapticMode.Native || nativeAnySource);

    /// <summary>Native mode uses a USB DualSense's 4-channel endpoint only for the game's HD haptics; every other
    /// source (sound, triggers, rumble) needs the virtual DualSense.</summary>
    public static bool UseUsbDualSense(bool usbPresent, NativeHapticSource source) => usbPresent && source == NativeHapticSource.Game;

    /// <summary>Settings written before the haptic source existed: HD PCM on = game, off = off.</summary>
    public static NativeHapticSource ResolveSource(NativeHapticSource? saved, bool legacyPcmEnabled) =>
        saved ?? (legacyPcmEnabled ? NativeHapticSource.Game : NativeHapticSource.Off);

    /// <summary>When to try again after a failed start. The virtual-Xbox audio bridge changes drivers and
    /// hides the pad, so it is never retried by itself; plain capture failures retry after <see cref="RetryMs"/>.</summary>
    public static long RetryAfterFailure(HapticMode mode, bool usesXboxGameBridge, long now) =>
        mode == HapticMode.Audio && usesXboxGameBridge ? Never : now + RetryMs;

    public static NativeSourcePlan PlanNativeSource(bool usbDualSensePresent, bool virtualInstalled, bool elevated, bool taskReady) =>
        usbDualSensePresent ? NativeSourcePlan.UsbDualSense
        : !virtualInstalled ? NativeSourcePlan.WaitForInstall
        : elevated ? NativeSourcePlan.VirtualHere
        : taskReady ? NativeSourcePlan.VirtualViaTask
        : NativeSourcePlan.WaitForClick;

    public static string? WaitNote(NativeSourcePlan plan) => plan switch
    {
        NativeSourcePlan.WaitForInstall => "Sanal DualSense için Ayarlar → Sanal DualSense → Kur.",
        NativeSourcePlan.WaitForClick => "Köprüyü başlatmak için \"Sanal köprüyü başlat\" düğmesine bas (bir kez Ayarlar'dan UAC'siz başlatmayı etkinleştirebilirsin).",
        _ => null
    };
}
