using System;
using System.Runtime.InteropServices;

namespace RW3ProNativeDSHaptics;

public enum ChromaEffect : byte
{
    Off = 0x00,
    Breathing = 0x02,
    Spectrum = 0x03,
    Wave = 0x04,
    Static = 0x0D
}

public sealed record BatteryStatus(bool Available, int Percent, bool IsCharging, string Source, string Label);

/// <summary>
/// Firmware-verified Razer Chroma RGB (Class 0x0F) and Battery/Power (Class 0x07 + XInput) commands for Wolverine V3 Pro.
/// All packet structures match NXP Dev2FW 2.04.00 handlers (0x2F700 for Class 0x0F, 0x2B4F4 validator, 0x2EA8E LED renderer,
/// and 0x17370 for Class 0x07) and captured Razer Controller Setup for Xbox / Synapse USB frames.
/// </summary>
public static class WolverineLightingAndBattery
{
    public const byte LightingClass = 0x0F;
    public const byte CmdSetEffect = 0x02;
    public const byte CmdSetBrightness = 0x04;
    public const byte CmdGetEffect = 0x82;
    public const byte CmdGetBrightness = 0x84;

    public const byte PowerClass = 0x07;
    public const byte CmdGetBattery = 0x80;
    public const byte CmdGetCharging = 0x84;

    public const byte ZoneLogo = 0x05;

    private static byte _tx = 0x20;
    private static readonly object SendGate = new();

    /// <summary>
    /// Optional delegate registered by the Xbox-mode GIP transport when running inside the Windows app,
    /// keeping this file free of WinRT dependencies for net10.0 contract tests.
    /// </summary>
    public static Func<byte[], (bool Ok, string? Error)>? GipPacketSender { get; set; }

    /// <summary>
    /// Optional delegate registered by the Windows app to query battery status via Windows.Gaming.Input (WGI)
    /// when XInput is not initialized or returns disconnected.
    /// </summary>
    public static Func<BatteryStatus?>? WgiBatteryReader { get; set; }

    /// <summary>
    /// Builds the argument payload for Class 0x0F Cmd 0x02 (Set Effect), verified against Dev2FW 0x2F9F0, 0x2B4F4, and 0x2EA8E:
    /// - Off (0x00), Breathing (0x02), Spectrum (0x03), Wave (0x04): [profile, zone 0x05, effectId, p1, p2, colorCount, RGB...]
    /// - Static (0x0D): [profile, zone 0x05, 0x0D, colorSlot=0x08, writeCustom=0x01, customCount=0x01, R, G, B]
    ///   (Dev2FW 0x2EA96 renders 0x0D via 0x2EB4A to all 4 LEDs; 0x08 is Custom Frame mode and is a no-op under 0F/02.)
    /// Note: Validator 0x2B4F4 checks p1 (byte[3]) for Wave (0x04 -> 1 or 2) and p2 (byte[4]) for Spectrum (0x03 -> 0..2).
    /// Renderer 0x2EA8E checks p1 (byte[3]) for Breathing (0x02 -> 0=random, 1=single color, 2=two colors).
    /// </summary>
    public static byte[] BuildEffectArgs(
        ChromaEffect effect,
        (byte R, byte G, byte B) color1,
        (byte R, byte G, byte B) color2 = default,
        int breathingMode = 1,
        int waveDirection = 1,
        int speed = 2,
        byte profile = 0)
    {
        if (profile > 4) throw new ArgumentOutOfRangeException(nameof(profile));
        if ((byte)effect == 0x08) effect = ChromaEffect.Static; // Migrate legacy saved enum value
        return effect switch
        {
            ChromaEffect.Off => [profile, ZoneLogo, (byte)ChromaEffect.Off, 0x00, 0x00, 0x00],
            ChromaEffect.Static => [profile, ZoneLogo, (byte)ChromaEffect.Static, 0x08, 0x01, 0x01, color1.R, color1.G, color1.B],
            ChromaEffect.Spectrum => [profile, ZoneLogo, (byte)ChromaEffect.Spectrum, 0x00, 0x00, 0x00],
            ChromaEffect.Wave => [
                profile, ZoneLogo, (byte)ChromaEffect.Wave,
                (byte)Math.Clamp(waveDirection, 1, 2), 0x00, 0x00
            ],
            ChromaEffect.Breathing => breathingMode switch
            {
                0 => [profile, ZoneLogo, (byte)ChromaEffect.Breathing, 0x00, 0x00, 0x00], // random colors
                2 => [profile, ZoneLogo, (byte)ChromaEffect.Breathing, 0x02, 0x00, 0x02, color1.R, color1.G, color1.B, color2.R, color2.G, color2.B],
                _ => [profile, ZoneLogo, (byte)ChromaEffect.Breathing, 0x01, 0x00, 0x01, color1.R, color1.G, color1.B]
            },
            _ => [profile, ZoneLogo, (byte)ChromaEffect.Off, 0x00, 0x00, 0x00]
        };
    }

    /// <summary>Builds the argument payload for Class 0x0F Cmd 0x04 (Set Brightness): [profile, zone 0x05, brightness 0..255].</summary>
    public static byte[] BuildBrightnessArgs(byte brightness, byte profile = 0)
    {
        if (profile > 4) throw new ArgumentOutOfRangeException(nameof(profile));
        return [profile, ZoneLogo, brightness];
    }

    /// <summary>Builds a 60-byte GIP 0x0F tunnel packet for any Razer command class/id.</summary>
    public static byte[] BuildGipPacket(byte transactionId, byte commandClass, byte commandId, ReadOnlySpan<byte> args)
    {
        if (args.Length > 48) throw new ArgumentOutOfRangeException(nameof(args));
        var packet = new byte[SensaXboxFrameEncoder.GipPacketLength];
        packet[0] = 0x00;
        packet[1] = transactionId;
        packet[5] = (byte)args.Length;
        packet[6] = commandClass;
        packet[7] = commandId;
        args.CopyTo(packet.AsSpan(8));
        byte xor = 0;
        for (int i = 2; i < 58; i++) xor ^= packet[i];
        packet[58] = xor;
        return packet;
    }

    /// <summary>Sends a Razer command in the active controller mode (PC mode via HID Feature Report 4, Xbox mode via GIP 0x0F).</summary>
    public static (bool Ok, string Message) SendCommand(byte commandClass, byte commandId, ReadOnlySpan<byte> args)
    {
        byte tx = unchecked(++_tx);
        var mode = WolverineModeControl.Detect();
        if (mode == WolverineControllerMode.Pc)
        {
            using var hid = new SensaHidTransport();
            if (!hid.Open())
                return (false, "PC modunda kolun kontrol arabirimi (COL04) açılamadı.");
            var report = RazerCommand.Build(tx, commandClass, commandId, args);
            if (!hid.SendRaw(report))
                return (false, "USB HID komutu gönderilemedi (hata " + hid.LastError + ").");
            return (true, "PC modu (Doğrudan USB) üzerinden uygulandı.");
        }
        if (mode == WolverineControllerMode.Xbox)
        {
            var sender = GipPacketSender;
            if (sender == null)
                return (false, "Xbox modu GIP taşıyıcısı hazır değil.");
            var pkt = BuildGipPacket(tx, commandClass, commandId, args);
            var (ok, err) = sender(pkt);
            if (!ok)
                return (false, "Xbox modu (GIP) hazır değil veya odak bekliyor (" + (err ?? "bağlanıyor") + ").");
            return (true, "Xbox modu (GIP 0x0F) üzerinden uygulandı.");
        }
        return (false, "Wolverine V3 Pro bulunamadı (kablo veya alıcıyla bağlı olmalı).");
    }

    /// <summary>Applies both brightness and lighting effect to the controller (RAM + optional profile slot).</summary>
    public static (bool Ok, string Message) ApplyLighting(
        ChromaEffect effect,
        int brightnessPercent,
        (byte R, byte G, byte B) color1,
        (byte R, byte G, byte B) color2 = default,
        int breathingMode = 1,
        int waveDirection = 1,
        int speed = 2,
        byte saveToProfile = 0)
    {
        lock (SendGate)
        {
            byte brightByte = (byte)Math.Round(Math.Clamp(brightnessPercent, 0, 100) * 255.0 / 100.0);
            var bArgs = BuildBrightnessArgs(effect == ChromaEffect.Off ? (byte)0 : brightByte, 0);
            var (okB, msgB) = SendCommand(LightingClass, CmdSetBrightness, bArgs);
            if (!okB) return (false, msgB);

            // Allow firmware main loop (0x1EF0C / GIP 0x0F receiver) to process the brightness packet before SetEffect.
            System.Threading.Thread.Sleep(55);

            var eArgs = BuildEffectArgs(effect, color1, color2, breathingMode, waveDirection, speed, 0);
            var (okE, msgE) = SendCommand(LightingClass, CmdSetEffect, eArgs);
            if (!okE) return (false, msgE);

            if (saveToProfile is >= 1 and <= 4)
            {
                System.Threading.Thread.Sleep(55);
                SendCommand(LightingClass, CmdSetBrightness, BuildBrightnessArgs(effect == ChromaEffect.Off ? (byte)0 : brightByte, saveToProfile));
                System.Threading.Thread.Sleep(55);
                SendCommand(LightingClass, CmdSetEffect, BuildEffectArgs(effect, color1, color2, breathingMode, waveDirection, speed, saveToProfile));
            }
            return (true, msgE);
        }
    }

    // ── Battery reading (PC HID Feature 07/80 & 07/84 + XInput GetBatteryInformation fallback) ──

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XInputBatteryInformation pBatteryInformation);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XInputStateDummy state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputStateDummy { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    /// <summary>
    /// Reads battery percentage and charging status:
    /// 1) In PC mode: queries Razer Class 0x07 Cmd 0x80 (battery 0..255 -> 0..100%) and Cmd 0x84 (charging 0/1) via HID feature report 4.
    /// 2) Fallback (Xbox mode / XInput): queries XInputGetBatteryInformation for the physical pad slot.
    /// </summary>
    public static BatteryStatus QueryBattery(int preferredXInputSlot = -1)
    {
        var mode = WolverineModeControl.Detect();
        if (mode == WolverineControllerMode.Pc)
        {
            if (TryQueryPcHidBattery(out int pct, out bool charging))
            {
                string text = charging ? $"⚡ %{pct} (Şarj)" : $"🔋 %{pct}";
                return new BatteryStatus(true, pct, charging, "Razer HID (07/80)", text);
            }
        }

        int slot = FindPhysicalXInputSlot(preferredXInputSlot);
        if (slot >= 0)
        {
            try
            {
                if (XInputGetBatteryInformation((uint)slot, 0 /*BATTERY_DEVTYPE_GAMEPAD*/, out var info) == 0)
                {
                    if (info.BatteryType == 1) // BATTERY_TYPE_WIRED
                        return new BatteryStatus(true, 100, true, "XInput (Kablolu)", "🔌 Kablolu / Şarj");
                    if (info.BatteryType is 2 or 3) // ALKALINE or NIMH
                    {
                        int pct = info.BatteryLevel switch { 0 => 10, 1 => 35, 2 => 70, _ => 100 };
                        string levelWord = info.BatteryLevel switch { 0 => "Çok Düşük", 1 => "Düşük", 2 => "Orta", _ => "Dolu" };
                        return new BatteryStatus(true, pct, false, "XInput", $"🔋 ~%{pct} ({levelWord})");
                    }
                }
            }
            catch { }
        }

        if (WgiBatteryReader?.Invoke() is { Available: true } wgiStatus)
            return wgiStatus;

        if (mode != WolverineControllerMode.NotFound)
        {
            // In Xbox mode without XInput/WGI battery report, do NOT show a false charging bolt ⚡;
            // show a clean wireless/connected battery indicator and explain in tooltip that exact % requires PC mode (07/80).
            return new BatteryStatus(true, -1, false,
                mode == WolverineControllerMode.Xbox ? "Xbox modu (GIP)" : "USB",
                mode == WolverineControllerMode.Xbox ? "🔋 Xbox Modu" : "🔋 Bağlı");
        }

        return new BatteryStatus(false, 0, false, "-", "Kol bağlı değil");
    }

    /// <summary>Decodes Razer Class 0x07 Cmd 0x80 response byte (0..255 -> 0..100%), matching firmware formula (raw * 100 / 255).</summary>
    public static int DecodeRazerBatteryByte(byte raw) => (int)Math.Round(raw * 100.0 / 255.0);

    private static bool TryQueryPcHidBattery(out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        using var hid = new SensaHidTransport();
        if (!hid.Open()) return false;

        byte tx1 = unchecked(++_tx);
        if (!hid.QueryCommand(RazerCommand.Build(tx1, PowerClass, CmdGetBattery, [0x00, 0x00]), out var resp1))
            return false;
        if (resp1[1] != 0x02 || resp1[7] != PowerClass || resp1[8] != CmdGetBattery)
            return false;
        percent = DecodeRazerBatteryByte(resp1[10]);

        byte tx2 = unchecked(++_tx);
        if (hid.QueryCommand(RazerCommand.Build(tx2, PowerClass, CmdGetCharging, [0x00, 0x00]), out var resp2) &&
            resp2[1] == 0x02 && resp2[7] == PowerClass && resp2[8] == CmdGetCharging)
        {
            charging = resp2[10] != 0;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapsEx
    {
        public byte Type, SubType;
        public ushort Flags, Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
        public ushort WL, WR, Vid, Pid, Version, Unk1;
        public uint Unk2;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint one, uint user, uint flags, out CapsEx caps);

    private static bool IsVirtualXInputSlot(uint slot)
    {
        try { return XInputGetCapabilitiesEx(1, slot, 0, out var c) == 0 && c.Vid == 0x045E && c.Pid is 0x028E or 0x0B13; }
        catch { return false; }
    }

    private static int FindPhysicalXInputSlot(int preferred)
    {
        try
        {
            if (preferred is >= 0 and <= 3 && !IsVirtualXInputSlot((uint)preferred) && XInputGetState((uint)preferred, out _) == 0)
                return preferred;
            for (int i = 0; i < 4; i++)
                if (!IsVirtualXInputSlot((uint)i) && XInputGetState((uint)i, out _) == 0)
                    return i;
        }
        catch { }
        return -1;
    }
}
