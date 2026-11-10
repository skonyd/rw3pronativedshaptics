using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Drives the Wolverine's actuators for the general calibration test. It writes to the devices directly and sends
/// silence when it stops, so the running modes must be stopped first (MainWindow.BeginTest).
/// <list type="bullet">
/// <item>PC mode (0A4C): Sensa palms through Direct USB (HID), body motors through XInput. There is no trigger-motor
/// route in this mode.</item>
/// <item>Xbox mode (0A3F): Sensa palms and trigger motors in one frame over GIP (<see cref="GipSensaTransport"/>), and
/// GameInput for the body motors and the GameInput trigger channel.</item>
/// </list>
/// </summary>
public sealed class CalibrationRig : IDisposable
{
    public CalCapabilities Caps { get; private set; } = new("yok", false, false, false, false);
    private SensaHidTransport? _hid;
    private GipSensaTransport? _gip;
    private GameInputRumble? _gi;
    private int _slot = -1;
    private readonly byte[] _pcFrame = new byte[SensaFrameEncoder.MaxFrameLength];
    private readonly byte[] _xFrame = new byte[SensaXboxFrameEncoder.MaxFrameLength];
    private const double TickSeconds = 0.033;

    public static async Task<CalibrationRig> OpenAsync(CancellationToken token)
    {
        var rig = new CalibrationRig();
        var mode = WolverineModeControl.Detect();
        for (uint i = 0; i < 4 && rig._slot < 0; i++) if (XInput.IsConnected(i)) rig._slot = (int)i;
        if (mode == WolverineControllerMode.Pc)
        {
            rig._hid = new SensaHidTransport();
            bool palms = rig._hid.Open();
            rig.Caps = new("PC", palms, false, false, rig._slot >= 0);
        }
        else if (mode == WolverineControllerMode.Xbox)
        {
            rig._gip = GipSensaTransport.Shared;
            for (int i = 0; i < 30 && !rig._gip.TryOpen(); i++) await Task.Delay(100, token);
            rig._gi = new GameInputRumble();
            for (int i = 0; i < 20 && !rig._gi.TryAcquire(); i++) await Task.Delay(100, token);
            bool sensa = rig._gip.IsOpen, gi = rig._gi.HasDevice;
            rig.Caps = new("Xbox", sensa, sensa, gi, gi || rig._slot >= 0);
        }
        DiagnosticsService.Info($"Calibration rig: mode {rig.Caps.Mode}, Sensa palms {rig.Caps.SensaPalms}, Sensa triggers {rig.Caps.SensaTriggers}, " +
            $"rumble triggers {rig.Caps.RumbleTriggers}, body {rig.Caps.Body}, XInput slot {rig._slot}.");
        return rig;
    }

    /// <summary>Plays one stimulus for its duration (33 ms ticks), then silence.</summary>
    public async Task PlayAsync(CalStimulus s, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < s.Seconds)
            {
                Tick(s, sw.Elapsed.TotalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), token);
            }
        }
        finally { await SilenceAsync(); }
    }

    private void Tick(CalStimulus s, double t)
    {
        var left = new List<SensaBand>(); var right = new List<SensaBand>();
        double trigL = 0, trigR = 0, big = 0, small = 0, giL = 0, giR = 0;
        int trigPitch = SensaXboxFrameEncoder.TriggerPitch;
        foreach (var tone in s.Tones)
        {
            double a = Math.Clamp(tone.LevelAt(t, s.Seconds), 0, 1);
            switch (tone.Actuator, tone.Path)
            {
                case (CalActuator.PalmLeft, _): left.Add(Band(a, tone.Pitch)); break;
                case (CalActuator.PalmRight, _): right.Add(Band(a, tone.Pitch)); break;
                case (CalActuator.TriggerLeft, CalPath.Sensa): trigL = Math.Max(trigL, a); trigPitch = tone.Pitch; break;
                case (CalActuator.TriggerRight, CalPath.Sensa): trigR = Math.Max(trigR, a); trigPitch = tone.Pitch; break;
                case (CalActuator.TriggerLeft, CalPath.Rumble): giL = Math.Max(giL, a); break;
                case (CalActuator.TriggerRight, CalPath.Rumble): giR = Math.Max(giR, a); break;
                case (CalActuator.BodyBig, _): big = Math.Max(big, a); break;
                case (CalActuator.BodySmall, _): small = Math.Max(small, a); break;
            }
        }
        if (_gip != null)
        {
            int n = SensaXboxFrameEncoder.Encode(left.ToArray(), right.ToArray(), null, null, trigL, trigR, _xFrame, trigPitch);
            _gip.Send(_xFrame.AsSpan(0, n));
            if (_gi?.HasDevice == true) _gi.Set(big, small, giL, giR);
            else if (_slot >= 0) XInput.SetMotors((uint)_slot, big, small);
        }
        else
        {
            if (_hid?.IsOpen == true)
            {
                int n = SensaFrameEncoder.EncodeBands(left.Take(3).ToArray(), right.Take(3).ToArray(), _pcFrame, null, null);
                _hid.Send(_pcFrame.AsSpan(0, n));
            }
            if (_slot >= 0) XInput.SetMotors((uint)_slot, big, small);
        }
    }

    private static SensaBand Band(double a, int hz) => new(a, a, a, a, SensaFrameEncoder.PitchOf(hz <= 0 ? 120 : hz));

    public async Task SilenceAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (_gip != null) _gip.Send(SensaXboxFrameEncoder.Silence());
                if (_hid?.IsOpen == true) _hid.Send(SensaFrameEncoder.Silence());
                _gi?.Set(0, 0, 0, 0);
                if (_slot >= 0) XInput.SetMotors((uint)_slot, 0, 0);
            }
            catch (Exception ex) { DiagnosticsService.Error("Calibration silence", ex); }
            await Task.Delay(35);
        }
    }

    public void Dispose()
    {
        try { SilenceAsync().Wait(500); } catch { }
        _hid?.Dispose();
        _gi?.Dispose();
    }
}
