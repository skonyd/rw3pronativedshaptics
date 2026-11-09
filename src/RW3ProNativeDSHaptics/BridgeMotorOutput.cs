using System;

namespace RW3ProNativeDSHaptics;

public enum XboxBodyPriority { Strongest, GameRumble, HapticPcm }

public sealed record XboxNativeMix(int PcmPercent = 100, int RumblePercent = 100, int TriggerPercent = 100,
    XboxBodyPriority Priority = XboxBodyPriority.Strongest)
{
    public BridgeMotorFrame Route(double rumbleL, double rumbleR, double pcmL, double pcmR,
        double triggerL, double triggerR, bool impulse)
    {
        static double Gain(double value, int percent) =>
            double.IsFinite(value) ? Math.Clamp(value, 0, 1) * HapticOutputLevel.Limit(percent) / 100 : 0;

        double Mix(double rumble, double pcm)
        {
            rumble = Gain(rumble, RumblePercent);
            pcm = Gain(pcm, PcmPercent);
            return Priority switch
            {
                XboxBodyPriority.GameRumble when rumble > 0 => rumble,
                XboxBodyPriority.HapticPcm when pcm > 0 => pcm,
                _ => Math.Max(rumble, pcm)
            };
        }

        return BridgeMotorFrame.Route(Mix(rumbleL, pcmL), Mix(rumbleR, pcmR), 0, 0,
            Gain(triggerL, TriggerPercent), Gain(triggerR, TriggerPercent), impulse);
    }
}

/// <summary>Game-authored haptic envelopes and trigger interpretation remain separate until routing.</summary>
public readonly record struct BridgeMotorFrame(double BodyLeft, double BodyRight, double TriggerLeft, double TriggerRight)
{
    private static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
    public BridgeMotorFrame Scale(int percent)
    {
        double level = HapticOutputLevel.Limit(percent) / 100.0;
        return new(Unit(BodyLeft) * level, Unit(BodyRight) * level,
            Unit(TriggerLeft) * level, Unit(TriggerRight) * level);
    }
    public static BridgeMotorFrame Route(double rumbleL, double rumbleR, double hapticL, double hapticR,
        double triggerL, double triggerR, bool triggerMotors) => triggerMotors
        ? new(Math.Max(Unit(rumbleL), Unit(hapticL)), Math.Max(Unit(rumbleR), Unit(hapticR)), Unit(triggerL), Unit(triggerR))
        : new(Math.Max(Math.Max(Unit(rumbleL), Unit(hapticL)), Unit(triggerL)),
            Math.Max(Math.Max(Unit(rumbleR), Unit(hapticR)), Unit(triggerR)), 0, 0);
}

/// <summary>Serialized delivery, live level changes, independent stale-input stop, no writes after Stop.</summary>
public sealed class BridgeMotorOutput
{
    public const long TimeoutMs = 250;
    private readonly object _gate = new();
    private readonly Action<BridgeMotorFrame> _write;
    private BridgeMotorFrame _frame;
    private long _lastTick;
    private bool _active, _stopped;
    private int _level = 100;
    public BridgeMotorOutput(Action<BridgeMotorFrame> write) => _write = write;
    public int Level
    {
        get { lock (_gate) return _level; }
        set { lock (_gate) { _level = HapticOutputLevel.Limit(value); if (!_stopped) Deliver(_frame.Scale(_level)); } }
    }
    public void Submit(BridgeMotorFrame frame, long now)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _frame = frame; _lastTick = now; _active = true;
            Deliver(frame.Scale(_level));
        }
    }
    public void Clear()
    {
        lock (_gate) { if (_stopped) return; _frame = default; _active = false; Deliver(default); }
    }
    public bool CheckTimeout(long now)
    {
        lock (_gate)
        {
            if (_stopped || !_active || now - _lastTick < TimeoutMs) return false;
            _frame = default; _active = false; Deliver(default); return true;
        }
    }
    public void Stop()
    {
        lock (_gate) { if (_stopped) return; _stopped = true; _frame = default; _active = false; Deliver(default); }
    }
    private void Deliver(BridgeMotorFrame frame)
    {
        try { _write(frame); }
        catch
        {
            _frame = default; _active = false; _stopped = true;
            try { _write(default); } catch { }
            throw;
        }
    }
}
