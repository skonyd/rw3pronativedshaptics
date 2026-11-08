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
/// Creating the virtual device needs administrator rights (HIDMaestro).
/// Live check: the virtual device's input reports are read back like a game reads them and compared
/// with the physical Xbox pad (<see cref="VirtualPadVerifier"/>); a lasting mismatch is logged and shown.
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
    /// <summary>Hide the physical Xbox pad from games while running (HidHide), restore on stop.</summary>
    public bool HidePhysicalPad { get; set; }
    public string HidHideNote { get; private set; } = "";
    /// <summary>HID path of the virtual DualSense (what games open); null until the readback found it.</summary>
    public string? VirtualHidPath => _readback?.DevicePath;
    /// <summary>Engine state for the periodic log (set by the owner).</summary>
    public Func<string>? EngineProbe { get; set; }
    /// <summary>Restarts the Sensa engine when it is not running; returns a note when it acted.</summary>
    public Func<string?>? EngineWatchdog { get; set; }
    /// <summary>Largest haptic sample (channels 3/4, 0..1) since <see cref="MarkPeaks"/>.</summary>
    public (float Left, float Right) PeaksSinceMark { get { lock (_peakGate) return (_markL, _markR); } }
    public void MarkPeaks() { lock (_peakGate) _markL = _markR = 0; }
    private readonly object _peakGate = new();
    private float _markL, _markR, _logL, _logR;
    private readonly System.Collections.Generic.Dictionary<byte, long> _r2Modes = new(), _l2Modes = new();

    // ── Xbox mode (no Sensa): haptic audio → body motors, trigger effects → impulse-trigger motors ──
    /// <summary>Haptic envelope → body motor scale in Xbox mode.</summary>
    public static double XboxHapticGain = 1.6;
    public WolverineControllerMode ControllerMode => _mode;
    public long XboxMotorWrites => _xbox?.Writes ?? 0;
    public string XboxStatus => _mode != WolverineControllerMode.Xbox ? "" :
        _xbox?.HasDevice == true ? "Xbox modu: tetik motorları + gövde motorları" : "Xbox modu: GameInput kolu bulamadı (" + (_xbox?.LastError ?? "") + ")";
    private volatile WolverineControllerMode _mode = WolverineControllerMode.NotFound;
    private GameInputRumble? _xbox;
    private float _envStateL, _envStateR;           // OnFrames thread only
    private float _envL, _envR;                     // published, under _peakGate
    private long _envTick;
    private System.Threading.Timer? _modeTimer;
    private const float EnvAttack = 0.0104f;        // ≈ 2 ms at 48 kHz
    private const float EnvRelease = 0.99974f;      // ≈ 80 ms
    /// <summary>Result of the live readback comparison with the physical pad.</summary>
    public string InputCheck
    {
        get
        {
            if (!IsRunning) return "";
            if (_readback == null) return _readbackNote;
            if (_verifier.CurrentProblem != null) return "GİRİŞ UYUŞMAZLIĞI: " + _verifier.CurrentProblem;
            if (_verifier.Checks == 0) return "giriş doğrulaması bekleniyor";
            return $"giriş Xbox koluyla eşleşiyor ({_verifier.Matches}/{_verifier.Checks})";
        }
    }
    public static bool HidHideInstalled
    {
        get { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide"); return k != null; }
    }

    private long _hapticBlocks, _outputReports, _triggerReports;
    private readonly TriggerEffectInterpreter _left = new(), _right = new();
    private readonly object _gate = new();
    private byte _rumbleLeft, _rumbleRight;
    private TriggerEffect _lastLeftEffect, _lastRightEffect;
    private Thread? _input;
    private volatile bool _stop;
    private HapticFeatureExtractor? _target;
    private int _slot;
    private readonly VirtualPadVerifier _verifier = new();
    private VirtualPadReadback? _readback;
    private string _readbackNote = "";
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double NowMs => _clock.Elapsed.TotalMilliseconds;
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
            var before = VirtualPadReadback.DualSensePaths();
            _ctrl = _ctx.CreateController(profile, "rw3pro-bridge");
            var audio = _ctrl.UsbAudio;
            if (audio?.Output != null) audio.Output.FramesReceived += OnFrames;
            _ctrl.OutputReceived += OnOutput;
            _stop = false;
            _input = new Thread(InputLoop) { IsBackground = true, Name = "RW3-VirtualDualSense", Priority = ThreadPriority.AboveNormal };
            _input.Start();
            IsRunning = true;
            _mode = WolverineModeControl.Detect();
            DiagnosticsService.Info("Virtual DualSense: controller mode " + _mode + ".");
            _modeTimer = new System.Threading.Timer(_ => CheckMode(), null, 2000, 2000);
            StartReadback(before);
            if (!HidHideInstalled)
                DiagnosticsService.Info("Virtual DualSense: HidHide not installed - the game can also see the Xbox pad (double input possible).");
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

    private void StartReadback(System.Collections.Generic.HashSet<string> before)
    {
        _readbackNote = "giriş doğrulaması: sanal aygıt aranıyor";
        var rb = new VirtualPadReadback(OnReadback, () => NowMs);
        System.Threading.Tasks.Task.Run(() =>
        {
            string? virtualPath = null;
            if (rb.Start(before))
            {
                _readback = rb;
                virtualPath = rb.DevicePath;
                DiagnosticsService.Info("Virtual DualSense readback: " + rb.DevicePath);
            }
            else
            {
                _readbackNote = "giriş doğrulaması yok (sanal aygıt okunamadı)";
                DiagnosticsService.Info("Virtual DualSense readback: virtual HID device not found/readable.");
                rb.Dispose();
            }
            if (HidePhysicalPad && !_stop) HidePad(virtualPath);
        });
    }

    private void HidePad(string? virtualPath)
    {
        if (!HidHideControl.Query().Ready) { HidHideNote = "HidHide yok: oyun Xbox kolunu da görebilir"; return; }
        try
        {
            HidHideNote = HidHideControl.HidePad(virtualPath);
            DiagnosticsService.Info("Virtual DualSense: " + HidHideNote);
        }
        catch (Exception ex)
        {
            HidHideNote = "HidHide uygulanamadı: " + ex.Message;
            DiagnosticsService.Error("HidHide hide", ex);
        }
    }

    private void CheckMode()
    {
        if (!IsRunning) return;
        WolverineControllerMode m;
        try { m = WolverineModeControl.Detect(); } catch { return; }
        if (m == _mode) return;
        var old = _mode;
        _mode = m;
        DiagnosticsService.Info($"Virtual DualSense: controller mode {old} → {m}.");
        // New mode = new device node: hide that one from games too.
        if (HidePhysicalPad && m != WolverineControllerMode.NotFound) HidePad(VirtualHidPath);
    }

    private void OnReadback(double t, DualSenseInputState actual)
    {
        var problem = _verifier.Observe(t, actual);
        if (problem != null) DiagnosticsService.Info("Virtual DualSense INPUT MISMATCH vs Xbox pad: " + problem);
    }

#if HAS_HIDMAESTRO
    private void OnFrames(HMAudioOutput output, ReadOnlyMemory<byte> pcm)
    {
        // Interleaved PCM16, roles speakerLeft, speakerRight, hapticLeft, hapticRight = the DualSense
        // rear-channel layout the extractor already reads (channels 2/3).
        if (output.Channels != 4 || output.BitsPerSample != 16) return;
        var span = pcm.Span;
        float pl = 0, pr = 0;
        for (int i = 0; i + 8 <= span.Length; i += 8)
        {
            pl = Math.Max(pl, Math.Abs((short)(span[i + 4] | (span[i + 5] << 8)) / 32768f));
            pr = Math.Max(pr, Math.Abs((short)(span[i + 6] | (span[i + 7] << 8)) / 32768f));
        }
        float el = _envStateL, er = _envStateR;
        for (int i = 0; i + 8 <= span.Length; i += 8)
        {
            float a = Math.Abs((short)(span[i + 4] | (span[i + 5] << 8)) / 32768f), b = Math.Abs((short)(span[i + 6] | (span[i + 7] << 8)) / 32768f);
            el = a > el ? el + (a - el) * EnvAttack * 8 : el * EnvRelease;
            er = b > er ? er + (b - er) * EnvAttack * 8 : er * EnvRelease;
        }
        _envStateL = el; _envStateR = er;
        lock (_peakGate)
        {
            _markL = Math.Max(_markL, pl); _markR = Math.Max(_markR, pr); _logL = Math.Max(_logL, pl); _logR = Math.Max(_logR, pr);
            _envL = el; _envR = er; _envTick = Environment.TickCount64;
        }
        _target?.Push(span, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
        Interlocked.Increment(ref _hapticBlocks);
    }

    private void OnOutput(HMController controller, HMOutputPacket packet)
    {
        Interlocked.Increment(ref _outputReports);
        if (packet.Source != HMOutputSource.HidOutput || packet.ReportId != DualSenseOutputReport.ReportId) return;
        if (!DualSenseOutputReport.TryParse(packet.Data.Span, out var r)) return;
        lock (_gate)
        {
            if (r.LeftTriggerValid) { _left.SetEffect(r.LeftTrigger); _lastLeftEffect = r.LeftTrigger; _l2Modes[r.LeftTrigger.Mode] = _l2Modes.GetValueOrDefault(r.LeftTrigger.Mode) + 1; }
            if (r.RightTriggerValid) { _right.SetEffect(r.RightTrigger); _lastRightEffect = r.RightTrigger; _r2Modes[r.RightTrigger.Mode] = _r2Modes.GetValueOrDefault(r.RightTrigger.Mode) + 1; }
            if (r.RumbleValid) { _rumbleLeft = r.RumbleLeft; _rumbleRight = r.RumbleRight; }
        }
        if (r.LeftTriggerValid || r.RightTriggerValid) Interlocked.Increment(ref _triggerReports);
    }

    private void InputLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        ushort lastL = 0, lastR = 0;
        double nextLog = 10, nextAcquire = 0;
        var state = new HMGamepadState();
        // Every axis the profile declares gets its rest value, so none falls back to the encoder default.
        var axes = new System.Collections.Generic.Dictionary<HMAxis, float>();
        try { foreach (var a in _ctrl!.Profile.AvailableAxes ?? []) axes[a] = 0.5f; } catch { }
        foreach (var kv in HMGamepadStateHelpers.StandardAxes(_ctrl!.Profile, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f)) axes[kv.Key] = kv.Value;
        state.Axes = axes;
        while (!_stop)
        {
            double now = sw.Elapsed.TotalSeconds, dt = now - last; last = now;
            if (XInput.GetState((uint)_slot, out var s) == 0 && _ctrl != null)
            {
                var g = s.Gamepad;
                var pad = new XInputPad(g.wButtons, g.bLeftTrigger, g.bRightTrigger, g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY);
                state.Buttons = MapButtons(g.wButtons);
                state.Hat = HatOf(DualSenseInputMapping.Hat(g.wButtons));
                // HIDMaestro axes are [0..1], 0.5 = centre (DualSenseInputMapping; a -1..1 range pinned the
                // sticks up/left in TLOU2, 2026-11-08).
                foreach (var kv in HMGamepadStateHelpers.StandardAxes(_ctrl.Profile,
                             DualSenseInputMapping.StickX(g.sThumbLX), DualSenseInputMapping.StickY(g.sThumbLY),
                             DualSenseInputMapping.StickX(g.sThumbRX), DualSenseInputMapping.StickY(g.sThumbRY),
                             DualSenseInputMapping.Trigger(g.bLeftTrigger), DualSenseInputMapping.Trigger(g.bRightTrigger)))
                    axes[kv.Key] = kv.Value;
                _verifier.RecordExpected(NowMs, DualSenseInputMapping.Expected(pad));
                try { _ctrl.SubmitState(in state); } catch (Exception ex) { Status = "Giriş gönderilemedi: " + ex.Message; }
                double lt, rt; byte rl, rr;
                lock (_gate) { lt = _left.Update(g.bLeftTrigger / 255.0, dt); rt = _right.Update(g.bRightTrigger / 255.0, dt); rl = _rumbleLeft; rr = _rumbleRight; }
                if (_mode == WolverineControllerMode.Xbox)
                {
                    // Xbox mode: trigger effects on the real impulse-trigger motors (GameInput works in the
                    // background); the haptic audio, which Sensa would play in PC mode, on the body motors.
                    _xbox ??= new GameInputRumble();
                    if (!_xbox.HasDevice && now >= nextAcquire) { _xbox.TryAcquire(); nextAcquire = now + 1; }
                    float el, er; long et;
                    lock (_peakGate) { el = _envL; er = _envR; et = _envTick; }
                    if (Environment.TickCount64 - et > 60) el = er = 0;   // no haptic audio arriving
                    double body = XboxHapticGain;
                    _xbox.Set(Math.Max(rl / 255.0, el * body), Math.Max(rr / 255.0, er * body), lt, rt);
                    if (lastL != 0 || lastR != 0) { var z = new XInput.Vibration(); XInput.XInputSetState((uint)_slot, ref z); lastL = lastR = 0; }
                }
                else
                {
                    if (_xbox?.HasDevice == true) _xbox.Forget();
                    // PC mode: no trigger motors reachable (XUSB): trigger effects become body rumble on that side.
                    ushort ml = (ushort)(Math.Max(rl / 255.0, lt) * 65535), mr = (ushort)(Math.Max(rr / 255.0, rt) * 65535);
                    if (ml != lastL || mr != lastR)
                    {
                        var v = new XInput.Vibration { wLeftMotorSpeed = ml, wRightMotorSpeed = mr };
                        XInput.XInputSetState((uint)_slot, ref v);
                        lastL = ml; lastR = mr;
                    }
                }
            }
            if (now >= nextLog)
            {
                nextLog = now + 10;
                string l2, r2;
                lock (_gate)
                {
                    static string Modes(System.Collections.Generic.Dictionary<byte, long> m) =>
                        m.Count == 0 ? "-" : string.Join(" ", System.Linq.Enumerable.Select(m, kv => $"0x{kv.Key:X2}x{kv.Value}"));
                    l2 = Modes(_l2Modes); r2 = Modes(_r2Modes); _l2Modes.Clear(); _r2Modes.Clear();
                }
                float wl, wr; lock (_peakGate) { wl = _logL; wr = _logR; _logL = _logR = 0; }
                string? healed = null;
                try { healed = EngineWatchdog?.Invoke(); } catch (Exception ex) { healed = "engine watchdog failed: " + ex.Message; }
                string engine = "";
                try { engine = EngineProbe?.Invoke() ?? ""; } catch { }
                DiagnosticsService.Info($"Virtual DualSense: haptic blocks {HapticBlocks} (peak L {wl:F2} R {wr:F2}), output reports {OutputReports}, trigger reports {TriggerReports} " +
                    $"(L2 modes {l2}; R2 modes {r2}), rumble {_rumbleLeft}/{_rumbleRight}, " +
                    $"readback {_readback?.Reports ?? 0} reports, match {_verifier.Matches}/{_verifier.Checks}; mode {_mode}" +
                    (_mode == WolverineControllerMode.Xbox ? $" (GameInput {(_xbox?.HasDevice == true ? "ok" : "no device: " + _xbox?.LastError)}, writes {XboxMotorWrites})" : "") + $"; {engine}" + (healed != null ? "; " + healed : ""));
            }
            Thread.Sleep(4);
        }
        var off = new XInput.Vibration();
        XInput.XInputSetState((uint)_slot, ref off);
        _xbox?.Dispose(); _xbox = null;
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
        if ((b & 0x0400) != 0) r |= HMButton.Guide;   // Xbox button -> PS (XInputGetStateEx only)
        return r;
    }

    private static HMHat HatOf(byte hat) => hat switch
    {
        0 => HMHat.North, 1 => HMHat.NorthEast, 2 => HMHat.East, 3 => HMHat.SouthEast,
        4 => HMHat.South, 5 => HMHat.SouthWest, 6 => HMHat.West, 7 => HMHat.NorthWest, _ => HMHat.None
    };
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
        _modeTimer?.Dispose(); _modeTimer = null;
        _readback?.Dispose(); _readback = null;
        if (IsElevated && HidHideControl.RestorePending)
        {
            try { HidHideNote = HidHideControl.Restore(); DiagnosticsService.Info("Virtual DualSense: " + HidHideNote); }
            catch (Exception ex) { HidHideNote = "HidHide geri alınamadı: " + ex.Message; DiagnosticsService.Error("HidHide restore", ex); }
        }
        if (_verifier.FirstProblem != null) DiagnosticsService.Info("Virtual DualSense: first input mismatch this run: " + _verifier.FirstProblem);
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
        // Undocumented but long-stable ordinal 100: same state plus the Guide (Xbox) button bit 0x0400.
        [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint XInputGetStateEx(uint index, out State state);
        private static bool _exMissing;
        public static uint GetState(uint index, out State state)
        {
            if (!_exMissing)
            {
                try { return XInputGetStateEx(index, out state); }
                catch (EntryPointNotFoundException) { _exMissing = true; }
            }
            return XInputGetState(index, out state);
        }
        [DllImport("xinput1_4.dll")] public static extern uint XInputSetState(uint index, ref Vibration vibration);
    }
}
