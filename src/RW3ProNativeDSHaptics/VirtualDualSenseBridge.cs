using System;
using System.Runtime.InteropServices;
using System.Threading;
#if HAS_HIDMAESTRO
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Virtual DualSense bridge (HIDMaestro "dualsense-composite"): games see a real-looking USB DualSense
/// with its 4-channel audio interface, so no physical DualSense is needed.
///   • input: the Wolverine (XInput) → virtual DualSense buttons/sticks/triggers, ~250 Hz;
///   • haptics: the game's haptic PCM (channels 3/4) arrives via FramesReceived and goes straight
///     into the engine's feature extractor — the same input the Native path gets from loopback,
///     without loopback, Windows volume or a physical endpoint;
///   • triggers: output report 0x02 → TriggerEffectInterpreter → Wolverine rumble motors
///     (PC mode; Sensa stays on the palms), together with the game's DualSense rumble values.
/// Creating the virtual device needs administrator rights (HIDMaestro). Not verified with a game yet;
/// HidHide (to hide the Wolverine from the game and avoid double input) is not configured here.
/// </summary>
public sealed class VirtualDualSenseBridge : IDisposable
{
    public string Status { get; private set; } = "Durdu";
    public bool IsRunning { get; private set; }
    public long HapticBlocks => Interlocked.Read(ref _hapticBlocks);
    public long OutputReports => Interlocked.Read(ref _outputReports);
    public long TriggerReports => Interlocked.Read(ref _triggerReports);
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    private long _hapticBlocks, _outputReports, _triggerReports;
    private readonly TriggerEffectInterpreter _left = new(), _right = new();
    private readonly object _gate = new();
    private byte _rumbleLeft, _rumbleRight;
    private Thread? _input;
    private volatile bool _stop;
    private HapticFeatureExtractor? _target;
    private int _slot;
#if HAS_HIDMAESTRO
    private HMContext? _ctx;
    private HMController? _ctrl;
#endif

    public bool Start(HapticFeatureExtractor target, int xinputSlot)
    {
        Stop();
        _target = target; _slot = Math.Clamp(xinputSlot, 0, 3);
#if HAS_HIDMAESTRO
        if (!IsElevated) { Status = "Yönetici yetkisi gerekli (uygulamayı yönetici olarak yeniden başlat)."; return false; }
        try
        {
            _ctx = new HMContext();
            _ctx.LoadDefaultProfiles();
            if (!_ctx.IsDriverInstalled || !HMContext.IsUsbipBackendAvailable) { Status = "HIDMaestro kurulu değil (Ayarlar → Sanal DualSense → Kur)."; Stop(); return false; }
            var profile = _ctx.GetProfile(VirtualControllerSetup.DualSenseCompositeProfile)
                          ?? throw new InvalidOperationException("dualsense-composite profili yok");
            _ctrl = _ctx.CreateController(profile, "rw3pro-bridge");
            var audio = _ctrl.UsbAudio;
            if (audio?.Output != null) audio.Output.FramesReceived += OnFrames;
            _ctrl.OutputReceived += OnOutput;
            _stop = false;
            _input = new Thread(InputLoop) { IsBackground = true, Name = "RW3-VirtualDualSense", Priority = ThreadPriority.AboveNormal };
            _input.Start();
            IsRunning = true;
            Status = "Sanal DualSense çalışıyor" + (audio?.Output != null ? $" • titreşim sesi {audio.Output.Channels} kanal" : " • ses arabirimi yok");
            DiagnosticsService.Info("Virtual DualSense bridge started (XInput slot " + _slot + ").");
            return true;
        }
        catch (Exception ex)
        {
            Status = "Sanal DualSense başlatılamadı: " + ex.Message;
            DiagnosticsService.Error("Virtual DualSense bridge", ex);
            Stop();
            return false;
        }
#else
        Status = "HIDMaestro SDK bu derlemede yok.";
        return false;
#endif
    }

#if HAS_HIDMAESTRO
    private void OnFrames(HMAudioOutput output, ReadOnlyMemory<byte> pcm)
    {
        // Interleaved PCM16, roles speakerLeft, speakerRight, hapticLeft, hapticRight = the DualSense
        // rear-channel layout the extractor already reads (channels 2/3).
        if (output.Channels != 4 || output.BitsPerSample != 16) return;
        _target?.Push(pcm.Span, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
        Interlocked.Increment(ref _hapticBlocks);
    }

    private void OnOutput(HMController controller, HMOutputPacket packet)
    {
        Interlocked.Increment(ref _outputReports);
        if (packet.Source != HMOutputSource.HidOutput || packet.ReportId != DualSenseOutputReport.ReportId) return;
        if (!DualSenseOutputReport.TryParse(packet.Data.Span, out var r)) return;
        lock (_gate)
        {
            if (r.LeftTriggerValid) _left.SetEffect(r.LeftTrigger);
            if (r.RightTriggerValid) _right.SetEffect(r.RightTrigger);
            if (r.RumbleValid) { _rumbleLeft = r.RumbleLeft; _rumbleRight = r.RumbleRight; }
        }
        if (r.LeftTriggerValid || r.RightTriggerValid) Interlocked.Increment(ref _triggerReports);
    }

    private void InputLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        ushort lastL = 0, lastR = 0;
        var state = new HMGamepadState();
        while (!_stop)
        {
            double now = sw.Elapsed.TotalSeconds, dt = now - last; last = now;
            if (XInput.XInputGetState((uint)_slot, out var s) == 0 && _ctrl != null)
            {
                var g = s.Gamepad;
                state.Buttons = MapButtons(g.wButtons);
                state.Hat = MapHat(g.wButtons);
                float N(short v) => Math.Clamp(v / 32767f, -1f, 1f);
                // HID convention: Y grows downward (XInput: upward). Not yet verified in a game.
                state.Axes = HMGamepadStateHelpers.StandardAxes(_ctrl.Profile, N(g.sThumbLX), -N(g.sThumbLY), N(g.sThumbRX), -N(g.sThumbRY),
                    g.bLeftTrigger / 255f, g.bRightTrigger / 255f);
                try { _ctrl.SubmitState(in state); } catch (Exception ex) { Status = "Giriş gönderilemedi: " + ex.Message; }
                double lt, rt; byte rl, rr;
                lock (_gate) { lt = _left.Update(g.bLeftTrigger / 255.0, dt); rt = _right.Update(g.bRightTrigger / 255.0, dt); rl = _rumbleLeft; rr = _rumbleRight; }
                // Wolverine has no adaptive triggers: trigger effects become rumble on that side.
                ushort ml = (ushort)(Math.Max(rl / 255.0, lt) * 65535), mr = (ushort)(Math.Max(rr / 255.0, rt) * 65535);
                if (ml != lastL || mr != lastR)
                {
                    var v = new XInput.Vibration { wLeftMotorSpeed = ml, wRightMotorSpeed = mr };
                    XInput.XInputSetState((uint)_slot, ref v);
                    lastL = ml; lastR = mr;
                }
            }
            Thread.Sleep(4);
        }
        var off = new XInput.Vibration();
        XInput.XInputSetState((uint)_slot, ref off);
    }

    private static HMButton MapButtons(ushort b)
    {
        HMButton r = HMButton.None;
        if ((b & 0x1000) != 0) r |= HMButton.A;
        if ((b & 0x2000) != 0) r |= HMButton.B;
        if ((b & 0x4000) != 0) r |= HMButton.X;
        if ((b & 0x8000) != 0) r |= HMButton.Y;
        if ((b & 0x0100) != 0) r |= HMButton.LeftBumper;
        if ((b & 0x0200) != 0) r |= HMButton.RightBumper;
        if ((b & 0x0020) != 0) r |= HMButton.Back;
        if ((b & 0x0010) != 0) r |= HMButton.Start;
        if ((b & 0x0040) != 0) r |= HMButton.LeftStick;
        if ((b & 0x0080) != 0) r |= HMButton.RightStick;
        return r;
    }

    private static HMHat MapHat(ushort b)
    {
        bool up = (b & 1) != 0, down = (b & 2) != 0, left = (b & 4) != 0, right = (b & 8) != 0;
        return (up, down, left, right) switch
        {
            (true, false, false, true) => HMHat.NorthEast, (false, true, false, true) => HMHat.SouthEast,
            (false, true, true, false) => HMHat.SouthWest, (true, false, true, false) => HMHat.NorthWest,
            (true, false, _, _) => HMHat.North, (false, true, _, _) => HMHat.South,
            (_, _, false, true) => HMHat.East, (_, _, true, false) => HMHat.West, _ => HMHat.None
        };
    }
#endif

    public void Stop()
    {
        _stop = true;
        _input?.Join(500);
        _input = null;
#if HAS_HIDMAESTRO
        try { _ctrl?.Dispose(); } catch { }
        try { _ctx?.Dispose(); } catch { }
        _ctrl = null; _ctx = null;
#endif
        if (IsRunning) DiagnosticsService.Info("Virtual DualSense bridge stopped.");
        IsRunning = false;
        if (Status.StartsWith("Sanal DualSense çalışıyor", StringComparison.Ordinal)) Status = "Durdu";
    }

    public void Dispose() => Stop();

    private static class XInput
    {
        [StructLayout(LayoutKind.Sequential)] public struct Gamepad { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
        [StructLayout(LayoutKind.Sequential)] public struct State { public uint dwPacketNumber; public Gamepad Gamepad; }
        [StructLayout(LayoutKind.Sequential)] public struct Vibration { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
        [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint index, out State state);
        [DllImport("xinput1_4.dll")] public static extern uint XInputSetState(uint index, ref Vibration vibration);
    }
}
