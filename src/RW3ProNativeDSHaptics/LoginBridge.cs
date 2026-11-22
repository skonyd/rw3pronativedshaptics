using System;
using System.Collections.Generic;
using System.Threading;
#if HAS_HIDMAESTRO
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Direct mode on the Windows sign-in screen (docs/WINUSB_DIRECT_MODE.md). In direct mode the Wolverine is on WinUSB,
/// and until the user signs in and RW3 starts nothing reads it, so the sign-in screen saw no pad (2026-11-23). This runs
/// RW3 without a window as SYSTEM from a boot task (<see cref="LoginBridgeTask"/>): it reads the pad and feeds a
/// virtual Xbox Series pad with buttons and sticks only (no rumble, Sensa or audio). When RW3 starts in the user's
/// session it asks for the pad (<see cref="DirectModeOwnership"/>) and this lets go; when that RW3 ends (exit, crash,
/// sign-out) this takes the pad back.
/// </summary>
public static class LoginBridge
{
    public const string Arg = "--login-bridge";
    /// <summary>The pad the sign-in screen sees: the same profile RW3 uses for its Xbox Series output.</summary>
    public const string Profile = "xbox-series-xs-bt";
    /// <summary>After handing the pad to RW3, wait this long before asking for it again (RW3 retries every 5 s).</summary>
    private const int CooldownMs = 10_000;

    public static int Run()
    {
        WinUsbGipTransport.IsLoginBridge = true;
        WinUsbGipTransport.Log = m => DiagnosticsService.Info("Login bridge: " + m);
        DiagnosticsService.Info("Login bridge: started (" + Environment.UserName + ", session " + System.Diagnostics.Process.GetCurrentProcess().SessionId + ").");
        IntPtr mutex = DirectModeOwnership.Create(isMutex: true), release = DirectModeOwnership.Create(isMutex: false);
        IntPtr stop = LoginBridgeTask.CreateStopEvent();   // set by LoginBridgeTask before it ends or replaces the task
        if (mutex == IntPtr.Zero || release == IntPtr.Zero || stop == IntPtr.Zero)
        {
            DiagnosticsService.Info("Login bridge: cannot create the owner objects (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); exiting.");
            return 2;
        }
        bool Stopping() => DirectModeOwnership.WaitForSingleObject(stop, 0) == DirectModeOwnership.WaitObject0;
        while (!Stopping())
        {
            if (!WinUsbGipTransport.DevicePresent()) { Thread.Sleep(2000); continue; }
            uint w = DirectModeOwnership.WaitForSingleObject(mutex, 1000);   // RW3 holds it while it runs
            if (w is not (DirectModeOwnership.WaitObject0 or DirectModeOwnership.WaitAbandoned)) continue;
            DirectModeOwnership.ResetEvent(release);   // a request left over from before is not for this round
            string why;
            try { why = Drive(release, stop); }
            catch (Exception ex) { why = "error: " + ex.Message; }
            WinUsbGipTransport.Shared.Close();
            DirectModeOwnership.ReleaseMutex(mutex);
            DiagnosticsService.Info("Login bridge: pad released (" + why + ").");
            // after an error wait 30 s: a virtual pad created and removed in a tight loop broke Windows' game input
            // until a restart once (memory: GameInput stale after virtual pad churn)
            int pause = why == "RW3 asked for the pad" ? CooldownMs : why.StartsWith("error") ? 30_000 : 2000;
            for (int i = 0; i < pause / 200 && !Stopping(); i++) Thread.Sleep(200);
        }
        DiagnosticsService.Info("Login bridge: stopped.");
        return 0;
    }

    /// <summary>Feeds the virtual pad until RW3 asks for the pad or the pad is gone; returns why it stopped.</summary>
    private static string Drive(IntPtr release, IntPtr stop)
    {
#if HAS_HIDMAESTRO
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        if (!ctx.IsDriverInstalled) return "HIDMaestro driver missing";
        var profile = ctx.GetProfile(Profile) ?? throw new InvalidOperationException("profile " + Profile + " missing");
        var transport = WinUsbGipTransport.Shared;
        long nextOpen = 0, lostSince = 0;
        using var ctrl = ctx.CreateController(profile, "rw3-login-bridge");
        var state = new HMGamepadState();
        var axes = new Dictionary<HMAxis, float>();
        try { foreach (var a in ctrl.Profile.AvailableAxes ?? []) axes[a] = 0.5f; } catch { }
        foreach (var kv in HMGamepadStateHelpers.StandardAxes(ctrl.Profile, .5f, .5f, .5f, .5f, 0f, 0f)) axes[kv.Key] = kv.Value;
        state.Axes = axes; state.Hat = HMHat.None;
        ctrl.SubmitState(in state);
        DiagnosticsService.Info("Login bridge: virtual " + Profile + " pad up.");
        while (true)
        {
            if (DirectModeOwnership.WaitForSingleObject(release, 0) == DirectModeOwnership.WaitObject0) return "RW3 asked for the pad";
            if (DirectModeOwnership.WaitForSingleObject(stop, 0) == DirectModeOwnership.WaitObject0) return "stop requested";
            long now = Environment.TickCount64;
            if (!transport.IsOpen && now >= nextOpen) { nextOpen = now + 1000; transport.TryOpen(); }
            if (transport.TryReadPad(out var pad))
            {
                lostSince = 0;
                state.Buttons = VirtualDualSenseBridge.MapButtons(pad.Buttons);
                state.Hat = VirtualDualSenseBridge.HatOf(DualSenseInputMapping.Hat(pad.Buttons));
                foreach (var kv in HMGamepadStateHelpers.StandardAxes(ctrl.Profile,
                             DualSenseInputMapping.StickX(pad.LX), DualSenseInputMapping.StickY(pad.LY),
                             DualSenseInputMapping.StickX(pad.RX), DualSenseInputMapping.StickY(pad.RY),
                             DualSenseInputMapping.Trigger(pad.LeftTrigger), DualSenseInputMapping.Trigger(pad.RightTrigger)))
                    axes[kv.Key] = kv.Value;
                ctrl.SubmitState(in state);
            }
            else
            {
                if (lostSince == 0) lostSince = now;
                if (!WinUsbGipTransport.DevicePresent() && now - lostSince > 5000) return "pad gone";
            }
            Thread.Sleep(4);
        }
#else
        return "built without HIDMaestro";
#endif
    }
}
