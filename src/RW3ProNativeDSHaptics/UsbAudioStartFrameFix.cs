using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

namespace RW3ProNativeDSHaptics;

/// <summary>Experiment (E-20261123-85) on HIDMaestro 2.1.0's USB/IP audio: the start frame it reports for a
/// completed isochronous URB. HIDMaestro answers each URB at the end of its last packet's window and reports that
/// moment as <c>start_frame</c> (<c>UsbAudioEngine.PumpPasses</c>: <c>FrameAtCompletion = (DueTick - epoch) / 1 ms</c>);
/// a real host controller reports the frame of the URB's first packet, PacketCount frames earlier. Windows' USB audio
/// driver drains the virtual DualSense's endpoint in 20 ms bursts while a real pad drains every 10 ms
/// (E-20261123-83/84), and TLOU2's TV sound loses 64 samples every 140 ms. This wraps HIDMaestro's private completion
/// callback so the reported frame is the first packet's. Armed only by
/// <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\ds-startframe.flag</c>; reflection on HIDMaestro internals, so any
/// mismatch leaves HIDMaestro as it is and says why.</summary>
public static class UsbAudioStartFrameFix
{
    public static string FlagFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "ds-startframe.flag");

    /// <param name="usbAudio">The controller's <c>UsbAudio</c> (HIDMaestro.HMUsbAudio).</param>
    public static string TryApply(object? usbAudio)
    {
        if (!File.Exists(FlagFile)) return "off";
        if (usbAudio == null) return "no audio";
        try
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            var engine = usbAudio.GetType().GetField("_engine", F)?.GetValue(usbAudio);
            if (engine == null) return "no engine field";
            var completeField = engine.GetType().GetField("_complete", F);
            if (completeField?.GetValue(engine) is not Delegate original) return "no completion field";
            var delegateType = completeField.FieldType;
            var invoke = delegateType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters();
            if (ps.Length != 3) return "unexpected completion signature";
            var urbType = ps[0].ParameterType;
            var frame = urbType.GetField("FrameAtCompletion");
            var count = urbType.GetField("PacketCount");
            if (frame == null || count == null || frame.FieldType != typeof(int) || count.FieldType != typeof(int)) return "unexpected URB fields";

            // (original, urb, data, per) => { urb.FrameAtCompletion -= urb.PacketCount; original(urb, data, per); }
            var dm = new DynamicMethod("RW3_FirstPacketFrame", typeof(void),
                new[] { delegateType, urbType, ps[1].ParameterType, ps[2].ParameterType }, engine.GetType().Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldfld, frame);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldfld, count);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stfld, frame);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Callvirt, invoke);
            il.Emit(OpCodes.Ret);
            completeField.SetValue(engine, dm.CreateDelegate(delegateType, original));
            return "applied (start frame = first packet's frame)";
        }
        catch (Exception ex) { return "failed: " + ex.GetType().Name + ": " + ex.Message; }
    }
}
