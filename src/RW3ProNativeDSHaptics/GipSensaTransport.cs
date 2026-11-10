using System;
using System.Threading;
using Windows.Gaming.Input;
using Windows.Gaming.Input.Custom;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Shared state for Sensa in Xbox mode: the setting, whether the engine is driving the controller over GIP, and the
/// trigger-motor strengths the virtual DualSense bridge publishes for the engine's next frame.
/// </summary>
public static class XboxSensa
{
    /// <summary>Settings page: in Xbox mode, drive palms and trigger motors through Sensa. Default on.</summary>
    public static volatile bool Enabled = true;
    /// <summary>The engine has an open GIP route and sends Xbox-mode Sensa frames.</summary>
    public static volatile bool Active;
    private static long _triggers, _triggerTick;

    /// <summary>Bridge → engine: trigger-motor strengths 0..1 (after mix and output level).</summary>
    public static void SetTriggers(double left, double right)
    {
        static int Q(double v) => (int)Math.Round((double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0) * 65535);
        Interlocked.Exchange(ref _triggers, ((long)Q(left) << 16) | (uint)Q(right));
        Interlocked.Exchange(ref _triggerTick, Environment.TickCount64);
    }

    /// <summary>The latest trigger strengths, or zero when the bridge has not published for <paramref name="maxAgeMs"/>.</summary>
    public static (double Left, double Right) Triggers(long maxAgeMs = 100)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _triggerTick) > maxAgeMs) return (0, 0);
        long v = Interlocked.Read(ref _triggers);
        return (((v >> 16) & 0xFFFF) / 65535.0, (v & 0xFFFF) / 65535.0);
    }
}

/// <summary>
/// Sends Sensa frames to the Wolverine V3 Pro in Xbox mode through Windows.Gaming.Input.Custom.
/// <list type="bullet">
/// <item>Route: a custom factory for Razer's GIP interface <c>{AAE22FB7-E167-4908-9A40-FF9C687FA986}</c> receives a
/// <see cref="GipGameControllerProvider"/>.</item>
/// <item>Packet: the Razer command (class 0x16, cmd 0x0E, args <c>[01, frame]</c>) is wrapped in GIP message 0x0F, the way
/// Razer Controller Setup for Xbox wraps its packets: 60 bytes <c>[status, tx, 0, 0, 0, size, class, cmd, args…]</c>, XOR of
/// bytes 2..57 at byte 58.</item>
/// </list>
/// Fire-and-forget: Windows does not hand the 0x10 answers to the sink. See docs/research/XBOX_MODE_HAPTICS_ROUTE.md.
/// </summary>
public sealed class GipSensaTransport
{
    public static readonly Guid RazerInterface = new("AAE22FB7-E167-4908-9A40-FF9C687FA986");
    public static GipSensaTransport Shared { get; } = new();

    private GipGameControllerProvider? _provider;
    private int _registered;
    private byte _tx;
    private readonly byte[] _packet = new byte[SensaXboxFrameEncoder.GipPacketLength];
    public long FramesSent { get; private set; }
    public string? LastError { get; private set; }

    public bool IsOpen => Volatile.Read(ref _provider) is { IsConnected: true };

    /// <summary>Registers the factory once (process-wide); returns whether a connected controller is available now.
    /// Windows hands the provider over asynchronously, so a first call usually returns false.</summary>
    public bool TryOpen()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            try { GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(new Factory(this), RazerInterface); }
            catch (Exception ex) { LastError = ex.Message; DiagnosticsService.Error("Xbox Sensa: GIP factory", ex); }
        }
        return IsOpen;
    }

    public bool Send(ReadOnlySpan<byte> frame)
    {
        var p = Volatile.Read(ref _provider);
        if (p == null) return false;
        try
        {
            SensaXboxFrameEncoder.BuildGipPacket(frame, _tx++, _packet);
            p.SendMessage(GipMessageClass.Command, 0x0F, _packet);
            FramesSent++;
            return true;
        }
        catch (Exception ex) { LastError = $"0x{ex.HResult:X8} {ex.Message}"; Volatile.Write(ref _provider, null); return false; }
    }

    private sealed class Factory(GipSensaTransport owner) : ICustomGameControllerFactory
    {
        public object CreateGameController(IGameControllerProvider provider)
        {
            if (provider is GipGameControllerProvider gip && gip.HardwareVendorId == 0x1532)
            {
                Volatile.Write(ref owner._provider, gip);
                DiagnosticsService.Info($"Xbox Sensa: GIP route to 1532:{gip.HardwareProductId:X4} (fw {gip.FirmwareVersionInfo.Major}.{gip.FirmwareVersionInfo.Minor}.{gip.FirmwareVersionInfo.Build}).");
            }
            return new Sink();
        }
        public void OnGameControllerAdded(IGameController value) { }
        public void OnGameControllerRemoved(IGameController value)
        {
            var p = Volatile.Read(ref owner._provider);
            if (p != null && !p.IsConnected) Volatile.Write(ref owner._provider, null);
        }
    }

    private sealed class Sink : IGipGameControllerInputSink
    {
        public void OnInputResumed(ulong timestamp) { }
        public void OnInputSuspended(ulong timestamp) { }
        public void OnKeyReceived(ulong timestamp, byte keyCode, bool isPressed) { }
        public void OnMessageReceived(ulong timestamp, GipMessageClass messageClass, byte messageId, byte sequenceId, byte[] messageBuffer) { }
    }
}
