using System;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Deterministic safety and evidence gates. These rules can be tested in CI
/// without a Windows audio device, Razer runtime, or controller attached.
/// </summary>
public static class HapticSafetyRules
{
    public const long AudioSilenceCutoffMs = 250;
    public const long RecentHapticEnergyMs = 2500;

    public static bool MustZeroStaleMotor(
        bool captureActive, bool motorNonzero, long lastAudioTick, long nowTick) =>
        captureActive && motorNonzero && lastAudioTick > 0 &&
        nowTick >= lastAudioTick && nowTick - lastAudioTick >= AudioSilenceCutoffMs;

    public static bool HasRecentChannelEnergy(
        bool captureActive, long lastLeftEnergyTick, long lastRightEnergyTick, long nowTick) =>
        captureActive &&
        (IsRecent(lastLeftEnergyTick, nowTick) || IsRecent(lastRightEnergyTick, nowTick));

    private static bool IsRecent(long tick, long nowTick) =>
        tick > 0 && nowTick >= tick && nowTick - tick < RecentHapticEnergyMs;

    // The opposite channel must remain quiet throughout the isolated fixture.
    public static bool IsolatedChannelObserved(
        long selectedBefore, long selectedAfter, long oppositeBefore, long oppositeAfter) =>
        selectedAfter > selectedBefore && oppositeAfter == oppositeBefore;
}
