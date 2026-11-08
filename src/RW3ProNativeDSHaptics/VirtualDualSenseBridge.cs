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
    ///   • input: Xbox mode uses device-bound GameInput; PC mode uses selected XInput → virtual DualSense;
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
    /// <summary>Xbox mode: trigger effects on the impulse-trigger motors (true) or, classic, on the body motors.</summary>
    public bool TriggerMotors { get; set; } = true;
    private int _outputLevelPercent = 100;
    public int OutputLevelPercent
    {
        get => Volatile.Read(ref _outputLevelPercent);
        set { Volatile.Write(ref _outputLevelPercent, HapticOutputLevel.Limit(value)); if (_motors != null) _motors.Level = value; }
    }
    private BridgeMotorOutput? _motors;
    private System.Threading.Timer? _motorWatchdog;
    private volatile bool _inputHealthy;
    private readonly object _framesGate = new();
    private string _triggerNote = "";
    private WolverineControllerMode _outputMode;
    private bool _interpreterForMotors;
    // Window maxima for the periodic log: physical trigger pull and what each motor was given.
    private double _wPullL, _wPullR, _wBodyL, _wBodyR, _wTrigL, _wTrigR;
    public WolverineControllerMode ControllerMode => _mode;
    public long XboxMotorWrites => _xbox?.Writes ?? 0;
    public string XboxStatus => _mode != WolverineControllerMode.Xbox ? "" :
        !_inputHealthy ? "Xbox modu: fiziksel giriş yok; motorlar durduruldu (" + (_xbox?.LastError ?? "aygıt aranıyor") + ")" :
        (TriggerMotors ? "Xbox modu: tetik motorları + gövde motorları" : "Xbox modu, klasik: hepsi gövde motorlarında") +
        (_triggerNote.Length > 0 ? "; " + _triggerNote : "");
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
            if (!_inputHealthy) return "Fiziksel kol girişi bekleniyor; motor çıkışı kapalı";
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
        if (_input?.IsAlive == true) return false;
        _target = target; _slot = Math.Clamp(xinputSlot, -1, 3);
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
            _mode = WolverineModeControl.Detect();
            _outputMode = _mode;
            _interpreterForMotors = false;
            lock (_gate)
                foreach (var interpreter in new[] { _left, _right })
                { interpreter.Reset(); interpreter.ClickSeconds = TriggerEffectInterpreter.DefaultClickSeconds; interpreter.TensionScale = 0; }
            _inputHealthy = false;
            _motors = new BridgeMotorOutput(WriteMotors) { Level = OutputLevelPercent };
            _motorWatchdog = new System.Threading.Timer(_ =>
            {
                try
                {
                if (_motors?.CheckTimeout(Environment.TickCount64) == true)
                {
                    _inputHealthy = false;
                    ClearGameState();
                    DiagnosticsService.Info("Virtual DualSense: input watchdog silenced motors.");
                }
                }
                catch (Exception ex) { _inputHealthy = false; _stop = true; Status = "Motor çıkışı durdu: " + ex.Message; DiagnosticsService.Error("Virtual motor watchdog", ex); }
            }, null, 20, 20);
            _stop = false;
            _input = new Thread(InputLoop) { IsBackground = true, Name = "RW3-VirtualDualSense", Priority = ThreadPriority.AboveNormal };
            _input.Start();
            IsRunning = true;
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
        if (!IsRunning || _stop) return;
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
        lock (_framesGate)
        {
        // Interleaved PCM16, roles speakerLeft, speakerRight, hapticLeft, hapticRight = the DualSense
        // rear-channel layout the extractor already reads (channels 2/3).
        if (_stop || output.Channels != 4 || output.BitsPerSample != 16 || pcm.Length % 8 != 0) return;
        if (!_inputHealthy) return;
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
        if (_mode == WolverineControllerMode.Pc)
            _target?.Push(span, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
        Interlocked.Increment(ref _hapticBlocks);
        }
    }

    private void OnOutput(HMController controller, HMOutputPacket packet)
    {
        if (_stop) return;
        Interlocked.Increment(ref _outputReports);
        if (packet.Source != HMOutputSource.HidOutput || packet.ReportId != DualSenseOutputReport.ReportId) return;
        if (!DualSenseOutputReport.TryParse(packet.Data.Span, out var r)) return;
        lock (_gate)
        {
            if (_stop) return;
            if (r.LeftTriggerValid) { _left.SetEffect(r.LeftTrigger); _lastLeftEffect = r.LeftTrigger; _l2Modes[r.LeftTrigger.Mode] = _l2Modes.GetValueOrDefault(r.LeftTrigger.Mode) + 1; }
            if (r.RightTriggerValid) { _right.SetEffect(r.RightTrigger); _lastRightEffect = r.RightTrigger; _r2Modes[r.RightTrigger.Mode] = _r2Modes.GetValueOrDefault(r.RightTrigger.Mode) + 1; }
            if (r.RumbleValid) { _rumbleLeft = r.RumbleLeft; _rumbleRight = r.RumbleRight; }
            string note = (!_left.Supported || !_right.Supported)
                ? $"Desteklenmeyen tetik efekti: L2 0x{_left.Mode:X2}, R2 0x{_right.Mode:X2} (bu efekt sessiz)" : "";
            if (note != _triggerNote) { _triggerNote = note; if (note.Length > 0) DiagnosticsService.Info("Virtual DualSense: " + note); }
        }
        if (r.LeftTriggerValid || r.RightTriggerValid) Interlocked.Increment(ref _triggerReports);
    }

    private void InputLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        double nextLog = 10, nextAcquire = 0;
        int xboxMisses = 0;
        var state = new HMGamepadState();
        // Every axis the profile declares gets its rest value, so none falls back to the encoder default.
        var axes = new System.Collections.Generic.Dictionary<HMAxis, float>();
        try { foreach (var a in _ctrl!.Profile.AvailableAxes ?? []) axes[a] = 0.5f; } catch { }
        foreach (var kv in HMGamepadStateHelpers.StandardAxes(_ctrl!.Profile, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f)) axes[kv.Key] = kv.Value;
        state.Axes = axes;
        try
        {
        state.Hat = HMHat.None;
        _ctrl.SubmitState(in state); // neutral even when physical input is initially unavailable
        while (!_stop)
        {
            double now = sw.Elapsed.TotalSeconds, dt = now - last; last = now;
            XInputPad pad = default;
            bool read = false;
            if (_mode != _outputMode)
            {
                _inputHealthy = false;
                _motors?.Clear(); // stop the old backend before selecting the new one
                _xbox?.Forget();
                _outputMode = _mode;
                ClearGameState();
                nextAcquire = 0;
            }
            if (_mode == WolverineControllerMode.Xbox)
            {
                _xbox ??= new GameInputRumble();
                if (!_xbox.HasDevice && now >= nextAcquire)
                {
                    nextAcquire = now + 1;
                    if (!_xbox.TryAcquire()) xboxMisses++;
                    if (!_xbox.HasDevice && xboxMisses == 3 && HidePhysicalPad && HidHideControl.RestorePending)
                    {
                        try { DiagnosticsService.Info("Virtual DualSense: GameInput cannot see the pad while hidden; " + HidHideControl.AllowApp(HidHideControl.GameInputServicePath)); }
                        catch (Exception ex) { DiagnosticsService.Error("HidHide allow GameInput", ex); }
                    }
                    if (_xbox.HasDevice) { DiagnosticsService.Info("Virtual DualSense: device-bound GameInput input/output 1532:0A3F."); xboxMisses = 0; }
                }
                read = _xbox.TryReadPad(out pad);
            }
            else if (_mode == WolverineControllerMode.Pc)
            {
                // Auto must not silently bind slot zero. With multiple pads require an explicit slot.
                int candidate = _slot;
                if (candidate < 0)
                {
                    for (int i = 0; i < 4; i++)
                        if (XInput.GetState((uint)i, out _) == 0) { if (candidate >= 0) { candidate = -1; break; } candidate = i; }
                }
                if (candidate >= 0 && XInput.GetState((uint)candidate, out var s) == 0)
                {
                    if (_pcOutputSlot >= 0 && _pcOutputSlot != candidate) _motors?.Clear();
                    _pcOutputSlot = candidate;
                    var v = s.Gamepad;
                    pad = new(v.wButtons, v.bLeftTrigger, v.bRightTrigger, v.sThumbLX, v.sThumbLY, v.sThumbRX, v.sThumbRY);
                    read = true;
                }
            }
            if (read && _ctrl != null && !_stop)
            {
                _inputHealthy = true;
                // Shared mapping below consumes either device-bound GameInput or the selected PC slot.
                var g = new XInput.Gamepad { wButtons = pad.Buttons, bLeftTrigger = pad.LeftTrigger, bRightTrigger = pad.RightTrigger,
                    sThumbLX = pad.LX, sThumbLY = pad.LY, sThumbRX = pad.RX, sThumbRY = pad.RY };
                _wPullL = Math.Max(_wPullL, g.bLeftTrigger / 255.0); _wPullR = Math.Max(_wPullR, g.bRightTrigger / 255.0);
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
                _ctrl.SubmitState(in state); // a failed virtual input submission must also stop physical output
                double lt, rt; byte rl, rr;
                bool motors = _mode == WolverineControllerMode.Xbox && TriggerMotors;
                if (motors != _interpreterForMotors)
                {
                    // Trigger motors: longer clicks and felt resistance; body motors: short clicks only.
                    lock (_gate)
                        foreach (var it in new[] { _left, _right })
                        { it.ClickSeconds = motors ? 0.09 : TriggerEffectInterpreter.DefaultClickSeconds; it.TensionScale = motors ? 0.45 : 0; }
                    _interpreterForMotors = motors;
                }
                lock (_gate) { lt = _left.Update(g.bLeftTrigger / 255.0, dt); rt = _right.Update(g.bRightTrigger / 255.0, dt); rl = _rumbleLeft; rr = _rumbleRight; }
                if (_outputMode == WolverineControllerMode.Xbox)
                {
                    // Xbox mode: trigger effects target impulse motors and haptic envelopes target body
                    // motors. API success does not establish background output; foreground loss remains open.
                    float el, er; long et;
                    lock (_peakGate) { el = _envL; er = _envR; et = _envTick; }
                    if (Environment.TickCount64 - et > 60) el = er = 0;   // no haptic audio arriving
                    double body = XboxHapticGain;
                    var routed = BridgeMotorFrame.Route(rl / 255.0, rr / 255.0, el * body, er * body, lt, rt, motors);
                    _motors?.Submit(routed, Environment.TickCount64);
                    var (bl, br, tl, tr) = routed.Scale(OutputLevelPercent);
                    _wBodyL = Math.Max(_wBodyL, Math.Min(1, bl)); _wBodyR = Math.Max(_wBodyR, Math.Min(1, br));
                    _wTrigL = Math.Max(_wTrigL, tl); _wTrigR = Math.Max(_wTrigR, tr);
                }
                else if (_outputMode == WolverineControllerMode.Pc)
                {
                    if (_xbox?.HasDevice == true) _xbox.Forget();
                    // PC mode: no trigger motors reachable (XUSB): trigger effects become body rumble on that side.
                    _motors?.Submit(BridgeMotorFrame.Route(rl / 255.0, rr / 255.0, 0, 0, lt, rt, false), Environment.TickCount64);
                }
            }
            else if (_inputHealthy)
            {
                _inputHealthy = false;
                _motors?.Clear(); ClearGameState();
                state.Buttons = HMButton.None; state.Hat = HMHat.None;
                foreach (var kv in HMGamepadStateHelpers.StandardAxes(_ctrl!.Profile, .5f, .5f, .5f, .5f, 0, 0)) axes[kv.Key] = kv.Value;
                try { _ctrl.SubmitState(in state); } catch { }
                DiagnosticsService.Info("Virtual DualSense: physical input unavailable; neutral input and zero motors.");
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
                try { if (_outputMode == WolverineControllerMode.Pc) healed = EngineWatchdog?.Invoke(); } catch (Exception ex) { healed = "engine watchdog failed: " + ex.Message; }
                string engine = "";
                try { engine = EngineProbe?.Invoke() ?? ""; } catch { }
                DiagnosticsService.Info($"Virtual DualSense: haptic blocks {HapticBlocks} (peak L {wl:F2} R {wr:F2}), output reports {OutputReports}, trigger reports {TriggerReports} " +
                    $"(L2 modes {l2}; R2 modes {r2}), rumble {_rumbleLeft}/{_rumbleRight}, " +
                    $"readback {_readback?.Reports ?? 0} reports, match {_verifier.Matches}/{_verifier.Checks}; mode {_mode}" +
                    (_mode == WolverineControllerMode.Xbox ? $" (GameInput {(_xbox?.HasDevice == true ? "ok" : "no device: " + _xbox?.LastError)}, writes {XboxMotorWrites}, " +
                        $"{(TriggerMotors ? "trigger motors" : "classic")}; max body {_wBodyL:F2}/{_wBodyR:F2} trig {_wTrigL:F2}/{_wTrigR:F2})" : "") +
                    $"; pull max L2 {_wPullL:F2} R2 {_wPullR:F2}; {_triggerNote}; {engine}" + (healed != null ? "; " + healed : ""));
                _wPullL = _wPullR = _wBodyL = _wBodyR = _wTrigL = _wTrigR = 0;
            }
            Thread.Sleep(4);
        }
        }
        catch (Exception ex) { Status = "Sanal DualSense giriş hatası: " + ex.Message; DiagnosticsService.Error("Virtual input", ex); }
        finally
        {
            _inputHealthy = false;
            try { _motors?.Stop(); }
            finally { ClearGameState(); _xbox?.Dispose(); _xbox = null; }
        }
    }

    private int _pcOutputSlot = -1;
    private void WriteMotors(BridgeMotorFrame f)
    {
        if (_outputMode == WolverineControllerMode.Xbox)
            _xbox?.Set(f.BodyLeft, f.BodyRight, f.TriggerLeft, f.TriggerRight);
        else if (_pcOutputSlot >= 0)
        {
            var v = new XInput.Vibration { wLeftMotorSpeed = (ushort)(f.BodyLeft * 65535), wRightMotorSpeed = (ushort)(f.BodyRight * 65535) };
            XInput.XInputSetState((uint)_pcOutputSlot, ref v);
        }
    }

    private void ClearGameState()
    {
        lock (_gate) { _left.Reset(); _right.Reset(); _rumbleLeft = _rumbleRight = 0; }
        lock (_framesGate)
        {
            lock (_peakGate) { _envStateL = _envStateR = _envL = _envR = 0; _envTick = 0; }
            _target?.ClearForSilence();
        }
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
        _inputHealthy = false;
        _motorWatchdog?.Dispose(); _motorWatchdog = null;
        try { _motors?.Stop(); } catch (Exception ex) { DiagnosticsService.Error("Virtual motor stop", ex); }
        if (_input != null && !_input.Join(2000))
        {
            Status = "Giriş iş parçacığı durmayı bekliyor; yeniden başlatma engellendi.";
            DiagnosticsService.Info("Virtual DualSense: " + Status);
            return; // retain native objects until the worker has actually exited
        }
        _input = null;
        _pcOutputSlot = -1;
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
