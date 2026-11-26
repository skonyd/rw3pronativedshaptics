using System;
using System.Threading;
using System.Threading.Tasks;
#if HAS_HIDMAESTRO
using System.Collections.Generic;
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>Opt-in virtual Xbox input and feedback with one physical four-motor writer.</summary>
public sealed class XboxAudioGameBridge : IDisposable
{
    private readonly GameInputRumble _physical = new();
    private readonly BridgeMotorOutput _output;
    private readonly object _gate = new();
#if HAS_HIDMAESTRO
    private HMContext? _context;
    private HMController? _controller;
#endif
    private AudioHapticsBridge? _audio;
    private Thread? _worker;
    private System.Threading.Timer? _watchdog;
    private BridgeMotorFrame _sound, _game;
    private long _soundAt, _gameAt;
    private long _reports;
    private volatile bool _muteTestAudio;
    private volatile bool _includeGameRumble = true;
    private volatile bool _includeGameTriggers = true;
    private bool _hideOwned;
    private volatile bool _running;
    private volatile string? _failure;

    /// <summary>Strict (experimental, VirtualDualSenseBridge.StrictHide + Xbox-mode Sensa): the physical pad is never touched
    /// through GameInput, so a GDK / Game Pass game cannot see it and plays the virtual Xbox pad only. Physical input and
    /// body motors go through XInput, the game's trigger values go to the trigger motors in the Sensa frame, and the sound
    /// goes to the palms through the Sensa engine (started by the orchestrator), not to the body motors.</summary>
    public bool Strict { get; init; }
    /// <summary>"RW3'ün tetik titreşimi": trigger-motor pulses from the hits of the game's rumble (and, in the engine, of the sound).</summary>
    public volatile bool SynthTriggers;
    private double _rumbleAvgL, _rumbleAvgR, _synthL, _synthR;
    /// <summary>Main page levels for the game's rumble (0..100 %) and trigger motors (0..200 %), applied live.</summary>
    public static volatile int RumblePercent = 100, TriggerPercent = 100;
    /// <summary>Strict path: which pad the game sees (HIDMaestro profile, <see cref="OutputMatrix.Profile"/>).</summary>
    public string VirtualProfile { get; init; } = "xbox-360-wired";
    private int _xinputSlot = -1;
    private int _stalls;
    /// <summary>Virtual Xbox/PS4 pads of this process that currently own a HidHide state.</summary>
    private static int s_hidersAlive;
    /// <summary>A virtual Xbox / PS4 pad of this process currently owns the HidHide state (others must not restore it).</summary>
    public static bool OwnsHide => Volatile.Read(ref s_hidersAlive) > 0;
    // XInput slots and GameInput devices present before our virtual pad was created: what appears after it is ours. A real
    // Bluetooth Xbox Series pad has the same 045E:0B13 as the virtual one, so VID/PID alone cannot tell them apart.
    private int _slotsBefore = -1;
    private HashSet<string>? _gameInputBefore = null;   // assigned only with HIDMaestro
    /// <summary>Strict path: the Sensa engine's state for the periodic log (set by the orchestrator).</summary>
    public Func<string>? EngineProbe { get; init; }
    private double _maxGameBody, _maxGameTrig;
    // Input path diagnostics (strict): physical read changes → virtual pad on XInput → GameInput reading (as a GDK game).
    private long _physChanges;
    // What the window's input looked like on each side (the physical pad, our virtual pad read back via XInput): shows
    // whether sticks, D-pad and triggers reach the virtual pad, not only that its packet number moves.
    private InputSeen _physSeen, _virtSeen;
    private struct InputSeen
    {
        private ushort _buttons; private int _stick, _trigger;
        public void Add(XInputPad p)
        {
            _buttons |= p.Buttons;
            _stick = Math.Max(_stick, Math.Max(Math.Max(Abs(p.LX), Abs(p.LY)), Math.Max(Abs(p.RX), Abs(p.RY))));
            _trigger = Math.Max(_trigger, Math.Max(p.LeftTrigger, p.RightTrigger));
        }
        private static int Abs(short v) => v == short.MinValue ? 32768 : Math.Abs((int)v);
        /// <summary>"btn 0x1001 stick 32767 trig 255" for the window, then reset.</summary>
        public string TakeNote()
        {
            string note = $"btn 0x{_buttons:X4} stick {_stick} trig {_trigger}";
            this = default;
            return note;
        }
    }
    private XInputPad _lastPad;
    private VirtualPadInputProbe? _inputProbe;
    private readonly InputDelayMeter _delay = new();
    private Thread? _probeThread;
    private int _virtualXSlot = -2;
    private uint _virtualXPacket0;
    private long _xinputWrites;
    private ushort _bodyL, _bodyR;
    private long _bodyTick;

    static XboxAudioGameBridge() => WinUsbGipTransport.Log = m => DiagnosticsService.Info(m);

    public XboxAudioGameBridge() => _output = new(frame =>
    {
        if (!Strict) { _physical.Set(frame.BodyLeft, frame.BodyRight, frame.TriggerLeft, frame.TriggerRight); return; }
        double rumble = Math.Clamp(RumblePercent, 0, 100) / 100.0;
        ushort l = (ushort)(BodyMotorLimit.Big(frame.BodyLeft * rumble) * 65535), r = (ushort)(Math.Clamp(frame.BodyRight * rumble, 0, 1) * 65535);
        long now = Environment.TickCount64;
        if (_direct)
        {
            // Direct mode: all four motors in one GIP rumble on the pad's own pipe, no focus gate. On change, else every
            // 100 ms while anything runs (the pad stops a rumble after its duration).
            double dtl = XboxNativeMix.TriggerGain(frame.TriggerLeft, TriggerPercent), dtr = XboxNativeMix.TriggerGain(frame.TriggerRight, TriggerPercent);
            bool any = (l | r) != 0 || dtl > .004 || dtr > .004;
            if (l != _bodyL || r != _bodyR || Math.Abs(dtl - _wgiL) > .01 || Math.Abs(dtr - _wgiR) > .01 || any && now - _bodyTick >= 100)
                if (WinUsbGipTransport.Shared.Rumble(l / 65535.0, r / 65535.0, dtl, dtr)) { _bodyL = l; _bodyR = r; _wgiL = dtl; _wgiR = dtr; _bodyTick = now; _xinputWrites++; }
            XboxSensa.SetTriggers(dtl, dtr);   // the Sensa engine may add its own trigger pulses on the same pipe
            XboxSensa.SetRumble(l / 65535.0, r / 65535.0);   // and plays the body rumble in the palms
            return;
        }
        // Only on change (or every 100 ms as a keep-alive): the loop runs every 8 ms.
        bool bodyWritten = false;
        if (_xinputSlot >= 0 && (l != _bodyL || r != _bodyR || (l | r) != 0 && now - _bodyTick >= 100))
        {
            var v = new XInput.Vibration { wLeftMotorSpeed = l, wRightMotorSpeed = r };
            if (XInput.XInputSetState((uint)_xinputSlot, ref v) == 0) { _bodyL = l; _bodyR = r; _bodyTick = now; _xinputWrites++; bodyWritten = true; }
        }
        double tl = XboxNativeMix.TriggerGain(frame.TriggerLeft, TriggerPercent), tr = XboxNativeMix.TriggerGain(frame.TriggerRight, TriggerPercent);
        XboxSensa.SetTriggers(tl, tr);   // Razer Sensa frames: cable only (the wireless adapter did not pass them)
        // The same triggers on the standard Xbox path too (wireless works, as with PowerA). An XInput write carries
        // trigger 0, so after one the triggers are written again; then on change or every 50 ms while they run.
        bool on = tl > .004 || tr > .004;
        if (on || _wgiTriggersOn)
        {
            if (bodyWritten || !on || Math.Abs(tl - _wgiL) > .01 || Math.Abs(tr - _wgiR) > .01 || now - _wgiTick >= 50)
                if (_wgi.Set(l / 65535.0, r / 65535.0, on ? tl : 0, on ? tr : 0)) { _wgiL = tl; _wgiR = tr; _wgiTick = now; _wgiTriggersOn = on; }
        }
    });
    private readonly WgiPadOutput _wgi = new();
    /// <summary>Direct mode: the pad is on WinUSB and RW3 speaks GIP to it (WinUsbGipTransport).</summary>
    private bool _direct;
    public bool Direct => _direct;
    private long _nextDirectOpen;
    private double _wgiL, _wgiR;
    private long _wgiTick;
    private bool _wgiTriggersOn;

    public bool IsRunning => _running && (Strict || _audio?.IsCapturing == true);
    public bool HasRecentSound => _audio?.HasRecentSound == true;
    public string? Failure => _failure ?? _audio?.Failure;
    public long FeedbackReports => Interlocked.Read(ref _reports);
    public long MotorWrites => _physical.Writes;
    public int OutputLevel { set => _output.Level = value; }
    public bool IncludeGameRumble { set => _includeGameRumble = value; }
    public bool IncludeGameTriggers { set => _includeGameTriggers = value; }

    public void Start(AudioHapticProfile profile) => StartCore(profile, synthetic: false);
    internal void StartSyntheticTest(AudioHapticProfile profile) => StartCore(profile, synthetic: true);

    public static async Task TestTriggerBackendsAsync(int outputLevel, IProgress<string> status, CancellationToken token)
    {
        using var input = new GameInputRumble();
        if (!input.TryAcquire() || !input.TryReadPad(out _))
            throw new InvalidOperationException("Xbox modunda tek Wolverine gerekli. " + input.LastError);
        double level = Math.Min(65, HapticOutputLevel.Limit(outputLevel)) / 100.0;
        if (level == 0) throw new InvalidOperationException("Test için titreşim seviyesini sıfırın üstüne getir.");
        await Pulse("GameInput · sol tetik", (l, r) => input.Set(0, 0, l, r), true);
        await Pulse("GameInput · sağ tetik", (l, r) => input.Set(0, 0, l, r), false);

        Windows.Gaming.Input.Gamepad? pad = null;
        for (int attempt = 0; attempt < 10 && pad == null; attempt++)
        {
            foreach (var candidate in Windows.Gaming.Input.Gamepad.Gamepads)
            {
                var raw = Windows.Gaming.Input.RawGameController.FromGameController(candidate);
                if (raw.HardwareVendorId != 0x1532 || raw.HardwareProductId != 0x0A3F) continue;
                if (pad != null) throw new InvalidOperationException("Windows.Gaming.Input birden fazla Wolverine görüyor.");
                pad = candidate;
            }
            if (pad == null) await Task.Delay(100, token);
        }
        if (pad == null) throw new InvalidOperationException("GameInput testi bitti. Windows.Gaming.Input Wolverine'i göremedi.");
        void WriteWgi(double l, double r) => pad.Vibration = new Windows.Gaming.Input.GamepadVibration
        {
            LeftTrigger = l, RightTrigger = r
        };
        await Pulse("Windows.Gaming.Input · sol tetik", WriteWgi, true);
        await Pulse("Windows.Gaming.Input · sağ tetik", WriteWgi, false);

        async Task Pulse(string label, Action<double, double> write, bool left)
        {
            status.Report(label);
            try
            {
                for (int i = 0; i < 12; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!input.TryReadPad(out _)) throw new InvalidOperationException("Wolverine bağlantısı kesildi.");
                    write(left ? level : 0, left ? 0 : level);
                    await Task.Delay(100, token);
                }
            }
            finally { write(0, 0); }
            await Task.Delay(500, token);
        }
    }

    public static async Task<(long Reports, long Writes)> RunSyntheticTestAsync(
        AudioHapticProfile profile, int outputLevel, IProgress<string>? status, CancellationToken token)
    {
        int level = Math.Min(65, HapticOutputLevel.Limit(outputLevel));
        if (level == 0) throw new InvalidOperationException("Test için titreşim seviyesini sıfırın üstüne getir.");
        using var bridge = new XboxAudioGameBridge { OutputLevel = level, _muteTestAudio = true };
        bridge.StartSyntheticTest(profile);
        using var playbackCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task playback = Task.CompletedTask;
        try
        {
            await Phase($"Yalnız sol tetik · %{level}", [100, 0, 0, 0, 255, 0, 235], 12);
            await Phase($"Yalnız sağ tetik · %{level}", [0, 100, 0, 0, 255, 0, 235], 12);
            bridge._muteTestAudio = false;
            playback = AudioPanTest.PlayAsync(AudioTestKind.Glide, null, playbackCancel.Token);
            await Phase("Yalnız ses gövdesi", [0, 0, 0, 0, 255, 0, 235], 12);
            await Phase($"Ses gövdesi + sol tetik · %{level}", [100, 0, 0, 0, 255, 0, 235], 12);
            await Phase($"Ses gövdesi + sağ tetik · %{level}", [0, 100, 0, 0, 255, 0, 235], 12);
            await Phase($"Ses gövdesi + oyun rumble + iki tetik · %{level}", [100, 100, 35, 35, 255, 0, 235], 12);
            await Phase("Tetikler kapalı, ses gövdesi devam", [0, 0, 0, 0, 255, 0, 235], 8);
            await playback;
            bridge._muteTestAudio = true;
            await Task.Delay(350, token);
            await Phase($"Ses bitti, yalnız sol tetik · %{level}", [100, 0, 0, 0, 255, 0, 235], 8);
            await Phase("Tüm kanallar kapalı", [0, 0, 0, 0, 255, 0, 235], 3);
            return (bridge.FeedbackReports, bridge.MotorWrites);
        }
        finally
        {
            playbackCancel.Cancel();
            try { await playback; }
            catch (OperationCanceledException) when (playbackCancel.IsCancellationRequested) { }
        }

        async Task Phase(string label, byte[] report, int repetitions)
        {
            status?.Report(label);
            for (int i = 0; i < repetitions; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!bridge.IsRunning) throw new InvalidOperationException(bridge.Failure ?? "Xbox test çıkışı durdu.");
                bridge.SubmitSyntheticReport(report);
                await Task.Delay(100, token);
            }
        }
    }

    private void StartCore(AudioHapticProfile profile, bool synthetic)
    {
        if (!synthetic && !Environment.IsPrivilegedProcess) throw new InvalidOperationException("Birleşik Xbox çıkışı için RW3'ü yönetici olarak aç.");
        if (!synthetic && HidHideControl.RestorePending && Interlocked.CompareExchange(ref s_hidersAlive, 0, 0) == 0)
        {
            // Left over from an earlier run that ended without undoing its hide (2026-11-15: blocked a restart).
            // No virtual pad of this process owns it, so undo it before hiding again.
            try { DiagnosticsService.Info("Virtual pad: stale HidHide state undone: " + HidHideControl.Restore()); }
            catch (Exception ex) { DiagnosticsService.Info("Virtual pad: stale HidHide restore failed: " + ex.Message); }
        }
        if (!synthetic && (!HidHideControl.Query().Ready || HidHideControl.RestorePending))
            throw new InvalidOperationException("HidHide hazır olmalı ve önceki gizleme geri alınmalı.");
        try
        {
            _direct = Strict && WinUsbGipTransport.DevicePresent() && WinUsbGipTransport.Shared.TryOpen();
            if (_direct) DiagnosticsService.Info("Virtual Xbox pad (strict): DIRECT MODE (WinUSB GIP pipe): input, rumble, triggers and Sensa on the pad's own pipe.");
            if (Strict) { if (!TryReadXInput(out _)) throw new InvalidOperationException(_direct ? "Doğrudan mod: Wolverine'in GIP borusu okunamıyor (" + WinUsbGipTransport.Shared.Summary + ")." : "Wolverine XInput ile görünmüyor (" + SlotTable() + ")."); }
            else if (!_physical.TryAcquire() || !_physical.TryReadPad(out _))
                throw new InvalidOperationException("Xbox modunda tek Wolverine gerekli. " + _physical.LastError);
            if (!synthetic)
            {
#if HAS_HIDMAESTRO
                _context = new HMContext();
                _context.LoadDefaultProfiles();
                if (!_context.IsDriverInstalled) throw new InvalidOperationException("HIDMaestro sürücüsü hazır değil.");
                // Strict: the output the user picked (Xbox 360 / Series / PS4). Input reaches GameInput (E-20261115-04);
                // which pad types a game accepts is game-specific (Banishers ignores even a real DualSense, E-20261121-01).
                string profileId = Strict ? VirtualProfile : "xbox-series-xs-bt";
                var model = _context.GetProfile(profileId) ??
                    throw new InvalidOperationException("Sanal kol profili bulunamadı: " + profileId + ".");
                if (Strict)
                {
                    _slotsBefore = 0;
                    for (int i = 0; i < 4; i++) if (XInput.GetState((uint)i, out _) == 0) _slotsBefore |= 1 << i;
                    if (VirtualPadInputProbe.Enabled) try { _gameInputBefore = VirtualPadInputProbe.DeviceIds(); } catch { }
                }
                _controller = _context.CreateController(model, "rw3-xbox-audio");
                _controller.OutputReceived += OnOutput;
                _hideOwned = true;
                Interlocked.Increment(ref s_hidersAlive);
                // Strict also runs in PC mode (the pad is XInput there too): hide whichever nodes the pad has now.
                // Direct mode: Windows' controller stack does not have the pad at all, nothing to hide from games
                if (!_direct) HidHideControl.HidePad(null, xboxOnly: !Strict || WolverineModeControl.Detect() == WolverineControllerMode.Xbox);
                if (Strict)
                    try { DiagnosticsService.Info("Virtual Xbox pad (strict): " + HidHideControl.DisallowApp(HidHideControl.GameInputServicePath)); }
                    catch (Exception ex) { DiagnosticsService.Info("Virtual Xbox pad (strict): GameInputSvc " + ex.Message); }
                if (!_direct && (!HidHideControl.RestorePending || !(Strict ? TryReadXInput(out _) : _physical.TryReadPad(out _))))
                    throw new InvalidOperationException("Gizleme sonrası Wolverine giriş yolu doğrulanamadı.");
#else
                throw new InvalidOperationException("HIDMaestro SDK bu derlemede yok.");
#endif
            }
            if (!Strict) _audio = new AudioHapticsBridge(new XboxAudioSink(v =>
            {
                lock (_gate)
                {
                    _sound = synthetic
                        ? new(Math.Min(v.LeftMotor, .40), Math.Min(v.RightMotor, .40), 0, 0)
                        : new(v.LeftMotor, v.RightMotor, 0, 0);
                    _soundAt = Environment.TickCount64;
                }
            })) { Profile = profile, DelayOverrideMs = synthetic ? 0 : null };
            _audio?.Start();
            _running = true;
            if (Strict) DiagnosticsService.Info("Virtual Xbox pad (strict): input/body via XInput slot " + _xinputSlot + ", game triggers → Sensa frame, sound → palms.");
            _watchdog = new System.Threading.Timer(_ =>
            {
                try
                {
                    // A stalled loop (window switch, Alt+Tab, a slow driver call) only silences the motors; the next frame
                    // resumes them. Stopping the whole bridge here (until 2026-11-22) undid the HidHide state and handed the
                    // game the real pad mid-session. Only a dead worker thread ends the bridge.
                    if (_output.CheckTimeout(Environment.TickCount64) && Interlocked.Increment(ref _stalls) <= 5)
                        DiagnosticsService.Info("Virtual pad: loop stalled > 250 ms, motors silenced (bridge keeps running).");
                    if (_running && _worker is { IsAlive: false }) Fail("Xbox köprü döngüsü durdu.");
                }
                catch (Exception ex) { Fail(ex.Message); }
            }, null, 25, 25);
            _worker = new Thread(Run) { IsBackground = true, Name = "RW3 Xbox game feedback" };
            _worker.Start();
        }
        catch { Dispose(); throw; }
    }

    // used with and without HIDMaestro (an SDK-free build, as CI builds, failed on them)
    private long _startedAt = Environment.TickCount64;

    /// <summary>XInput slot of the running strict virtual pad (-1 unknown). Above 0 while the Wolverine holds a lower
    /// slot: games that take player 1 from slot 0 then read the hidden Wolverine and ignore the virtual pad.</summary>
    public static volatile int VirtualSlot = -1;

#if HAS_HIDMAESTRO
    /// <summary>
    /// While the Wolverine is out (a replug), recreates the virtual pad when a lower XInput slot than its own is now free,
    /// so the game's player 1 (XInput slot 0 in Unreal games) is the virtual pad and the Wolverine comes back behind it.
    /// 2026-11-22: the Wolverine held slot 0 and the virtual pad slot 1; Banishers passed "press any button" but nothing
    /// moved, while the test range (reading every pad) saw full stick/trigger input from the virtual pad.
    /// </summary>
    private bool TryTakeLowerSlot(ref HMController? controller)
    {
        if (!Strict || _context == null || controller == null || _virtualXSlot <= 0 || _slotMoves >= 3) return false;
        int free = -1;
        for (int i = 0; i < _virtualXSlot; i++) if (XInput.GetState((uint)i, out _) != 0) { free = i; break; }
        if (free < 0) return false;
        int was = _virtualXSlot;
        _slotMoves++;
        var model = controller.Profile;
        controller.OutputReceived -= OnOutput;
        try { controller.Dispose(); } catch (Exception ex) { DiagnosticsService.Info("Virtual pad: old pad close " + ex.Message); }
        int before = 0;
        for (int i = 0; i < 4; i++) if (XInput.GetState((uint)i, out _) == 0) before |= 1 << i;
        controller = _context.CreateController(model, "rw3-xbox-audio");
        controller.OutputReceived += OnOutput;
        _controller = controller;
        _slotsBefore = before;
        _virtualXSlot = -1;
        // Pin the new slot now, before the Wolverine comes back (both would be "new" slots then).
        for (int n = 0; n < 40 && _virtualXSlot < 0; n++)
        {
            for (int i = 0; i < 4; i++)
                if ((before & (1 << i)) == 0 && XInput.GetState((uint)i, out var st) == 0) { _virtualXSlot = i; _virtualXPacket0 = st.dwPacketNumber; break; }
            if (_virtualXSlot < 0) Thread.Sleep(50);
        }
        VirtualSlot = _virtualXSlot;
        DiagnosticsService.Info($"Virtual Xbox pad (strict): recreated while the Wolverine is out, XInput slot {was} → {(_virtualXSlot >= 0 ? _virtualXSlot.ToString() : "?")} (player 1 = slot 0).");
        return true;
    }
    private int _slotMoves;

    private void OnOutput(HMController controller, HMOutputPacket packet)
    {
        LogNewShape(packet);
        if (packet.Source == HMOutputSource.HidOutput && AcceptReport(packet.ReportId, packet.Data.Span)) return;
        if (packet.Source == HMOutputSource.HidOutput && TryDecodeDualShock4(packet.ReportId, packet.Data.Span, out var ds4))
        {
            lock (_gate) { if (!_running) return; _game = ds4; _gameAt = Environment.TickCount64; Interlocked.Increment(ref _reports); }
            return;
        }
        var d = packet.Data.Span;
        if (TryDecodeXusb(d, out var xusb, out bool ledOnly))
        {
            lock (_gate) { if (!_running) return; _game = xusb; _gameAt = Environment.TickCount64; Interlocked.Increment(ref _reports); }
            if (Interlocked.Increment(ref _xusbLogged) <= 4)
                DiagnosticsService.Info($"Virtual Xbox 360 pad: rumble {Convert.ToHexString(d[..Math.Min(8, d.Length)])} -> big {xusb.BodyLeft:F2} small {xusb.BodyRight:F2}");
            return;
        }
        if (ledOnly) return;   // player-indicator LED command: normal, not rumble
        // Not a Series 0x0F report: log the first few unknown shapes.
        if (Interlocked.Increment(ref _unknownLogged) <= 6)
            DiagnosticsService.Info($"Virtual Xbox pad: unhandled output {packet.Source} id 0x{packet.ReportId:X2} len {packet.Data.Length}: " +
                Convert.ToHexString(packet.Data.Span[..Math.Min(16, packet.Data.Length)]));
    }
    private int _unknownLogged, _xusbLogged;

    /// <summary>
    /// Every distinct output packet (source, id, bytes) the game sends, up to 40 per pad: shows on which path and in which
    /// bytes Windows hands over trigger (impulse) rumble. 2026-11-22: the test range sent GameInput trigger rumble to the
    /// Series BT pad, body rumble arrived (0x0F report), trigger magnitudes were 0 in every decoded report.
    /// </summary>
    private void LogNewShape(HMOutputPacket packet)
    {
        if (!Strict || _shapes.Count >= 40) return;
        var d = packet.Data.Span;
        // shape = path + which bytes are non-zero (values vary all the time; a new non-zero byte, e.g. a trigger, is news)
        var mask = new char[Math.Min(24, d.Length)];
        for (int i = 0; i < mask.Length; i++) mask[i] = d[i] != 0 ? 'x' : '.';
        string key = $"{packet.Source} id 0x{packet.ReportId:X2} len {d.Length} {new string(mask)}";
        lock (_shapes) { if (_shapes.Count >= 40 || !_shapes.Add(key)) return; }
        DiagnosticsService.Info($"Virtual pad output shape: {key} e.g. {Convert.ToHexString(d[..Math.Min(24, d.Length)])}");
    }
    private readonly HashSet<string> _shapes = new();

    /// <summary>
    /// The Xbox 360 profile's rumble (HIDMaestro hands over the XUSB set-state): 5 bytes
    /// [device, LED state, big motor, small motor, flags], flags bit 0 = LED, bit 1 = vibration (the XInput driver's
    /// set-state request); or the 8-byte USB rumble report [00, 08, 00, big, small, 00, 00, 00]. Logged 2026-11-22:
    /// 00 08 00 00 01 and 00 0D 00 00 01 are LED commands (patterns 0x08 / 0x0D, flag 0x01), not rumble.
    /// </summary>
    internal static bool TryDecodeXusb(ReadOnlySpan<byte> d, out BridgeMotorFrame frame, out bool ledOnly)
    {
        frame = default; ledOnly = false;
        if (d.Length == 5)
        {
            if ((d[4] & 0x02) == 0) { ledOnly = (d[4] & 0x01) != 0; return false; }
            frame = new(d[2] / 255.0, d[3] / 255.0, 0, 0);
            return true;
        }
        if (d.Length >= 8 && d[0] == 0x00 && d[1] == 0x08) { frame = new(d[3] / 255.0, d[4] / 255.0, 0, 0); return true; }
        return false;
    }

    /// <summary>DualShock 4 USB output report 0x05 (data after the ID): [0] flags (bit 0 = rumble valid), [3] right/weak
    /// motor, [4] left/strong motor. No triggers.</summary>
    internal static bool TryDecodeDualShock4(byte reportId, ReadOnlySpan<byte> data, out BridgeMotorFrame frame)
    {
        frame = default;
        if (reportId != 0x05 || data.Length < 5 || (data[0] & 0x01) == 0) return false;
        frame = new(data[4] / 255.0, data[3] / 255.0, 0, 0);
        return true;
    }
#endif

    internal void SubmitSyntheticReport(ReadOnlySpan<byte> data)
    {
#if HAS_HIDMAESTRO
        if (_controller != null) throw new InvalidOperationException("Sentetik rapor yalnız test yolunda kullanılabilir.");
#endif
        AcceptReport(0x0F, data);
    }

    private bool AcceptReport(byte reportId, ReadOnlySpan<byte> data)
    {
        if (!_running || !XboxGameFeedback.TryDecode(reportId, data, out var frame)) return false;
        lock (_gate)
        {
            if (!_running) return true;
            _game = frame;
            _gameAt = Environment.TickCount64;
            Interlocked.Increment(ref _reports);
        }
        return true;
    }

    private void Run()
    {
        try
        {
#if HAS_HIDMAESTRO
            var controller = _controller;
            var axes = new Dictionary<HMAxis, float>();
            foreach (var axis in controller?.Profile.AvailableAxes ?? []) axes[axis] = .5f;
            var state = new HMGamepadState { Axes = axes };
#endif
            long nextLog = Environment.TickCount64 + 10_000, lostSince = 0;
            if (Strict && _inputProbe == null && VirtualPadInputProbe.Enabled)
            {
                // Own thread: GameInput's first enumeration blocks for a while, and the motor watchdog stops the bridge
                // when this loop pauses > 250 ms (2026-11-15).
                _inputProbe = new VirtualPadInputProbe { Exclude = _gameInputBefore };
                _probeThread = new Thread(() =>
                {
                    while (_running) { try { _inputProbe.Poll(); } catch { } Thread.Sleep(64); }
                }) { IsBackground = true, Name = "RW3 virtual pad input probe" };
                _probeThread.Start();
            }
            while (_running)
            {
                if (!Strict && _audio?.IsCapturing != true)
                    throw new InvalidOperationException(_audio?.Failure ?? "Ses yakalama durdu.");
                XInputPad pad;
                if (Strict)
                {
                    // Know our slot early (a replug right after start must already move the pad to slot 0).
                    if (_virtualXSlot == -2 && Environment.TickCount64 - _startedAt > 1500) VirtualXInputNote();
                    // A replug (which the strict setup asks for) drops XInput for a few seconds. Until 2026-11-15 that ended
                    // the bridge silently, HidHide was restored and the game went back to the physical pad.
                    if (!TryReadXInput(out pad))
                    {
                        long t = Environment.TickCount64;
                        if (lostSince == 0) { lostSince = t; DiagnosticsService.Info("Virtual Xbox pad (strict): Wolverine left XInput; waiting up to 60 s."); }
#if HAS_HIDMAESTRO
                        if (TryTakeLowerSlot(ref controller)) { pad = default; continue; }
#endif
                        if (t - lostSince > 60_000) throw new InvalidOperationException("Wolverine 60 sn XInput'ta görünmedi.");
                        pad = default;
                    }
                    else if (lostSince != 0) { lostSince = 0; DiagnosticsService.Info("Virtual Xbox pad (strict): Wolverine back on XInput slot " + _xinputSlot + "."); }
                }
                else if (!_physical.TryReadPad(out pad))
                    throw new InvalidOperationException("Wolverine girişi kesildi. " + _physical.LastError);
#if HAS_HIDMAESTRO
                if (controller != null)
                {
                    state.Buttons = VirtualDualSenseBridge.MapButtons(pad.Buttons);
                    state.Hat = VirtualDualSenseBridge.HatOf(DualSenseInputMapping.Hat(pad.Buttons));
                    foreach (var axis in HMGamepadStateHelpers.StandardAxes(controller.Profile,
                        DualSenseInputMapping.StickX(pad.LX), DualSenseInputMapping.StickY(pad.LY),
                        DualSenseInputMapping.StickX(pad.RX), DualSenseInputMapping.StickY(pad.RY),
                        DualSenseInputMapping.Trigger(pad.LeftTrigger), DualSenseInputMapping.Trigger(pad.RightTrigger)))
                        axes[axis.Key] = axis.Value;
                    controller.SubmitState(in state);
                    if (_direct) _delay.Note(WinUsbGipTransport.Shared.LastInputStamp);
                }
#endif
                long now = Environment.TickCount64;
                BridgeMotorFrame frame;
                lock (_gate)
                    frame = XboxGameFeedback.Mix(!_muteTestAudio && now - _soundAt < 250 ? _sound : default,
                        now - _gameAt < 1000 ? _game : default, _includeGameRumble, _includeGameTriggers);
                if (Strict && SynthTriggers)
                {
                    // the game's rumble hits (not its steady hum) → trigger motors, big motor left, small motor right
                    var g = now - _gameAt < 1000 ? _game : default;
                    double a = 1 - Math.Exp(-.008 / .35);
                    _rumbleAvgL += (g.BodyLeft - _rumbleAvgL) * a; _rumbleAvgR += (g.BodyRight - _rumbleAvgR) * a;
                    _synthL = Math.Max(_synthL * .93, Math.Clamp((g.BodyLeft - _rumbleAvgL) * 1.6, 0, 1));
                    _synthR = Math.Max(_synthR * .93, Math.Clamp((g.BodyRight - _rumbleAvgR) * 1.6, 0, 1));
                    frame = frame with { TriggerLeft = Math.Max(frame.TriggerLeft, _synthL), TriggerRight = Math.Max(frame.TriggerRight, _synthR) };
                }
                if (_running) _output.Submit(frame, now);
                if (Strict)
                {
                    if (!pad.Equals(_lastPad)) { _physChanges++; _lastPad = pad; }
                    _physSeen.Add(pad);
                    if (_virtualXSlot >= 0 && XInput.GetState((uint)_virtualXSlot, out var vs) == 0)
                        _virtSeen.Add(new(vs.Gamepad.wButtons, vs.Gamepad.bLeftTrigger, vs.Gamepad.bRightTrigger,
                            vs.Gamepad.sThumbLX, vs.Gamepad.sThumbLY, vs.Gamepad.sThumbRX, vs.Gamepad.sThumbRY));
                    _maxGameBody = Math.Max(_maxGameBody, Math.Max(_game.BodyLeft, _game.BodyRight));
                    _maxGameTrig = Math.Max(_maxGameTrig, Math.Max(_game.TriggerLeft, _game.TriggerRight));
                    if (now >= nextLog)
                    {
                        nextLog = now + 10_000;
                        string engine = ""; try { engine = EngineProbe?.Invoke() ?? ""; } catch { }
                        DiagnosticsService.Info($"Virtual {VirtualProfile} (strict): game reports {FeedbackReports}, game max body {_maxGameBody:F2} trig {_maxGameTrig:F2}, " +
                            $"{(_direct ? "DIRECT " + WinUsbGipTransport.Shared.Summary + ", " : "")}XInput writes {_xinputWrites} (slot {_xinputSlot}), WGI triggers {_wgi.State} writes {_wgi.Writes}, foreground takes pad {ControllerFocusGuard.ForegroundTakesController()}; " +
                            $"INPUT physical changes {_physChanges} ({_physSeen.TakeNote()}) → virtual XInput {VirtualXInputNote()} ({_virtSeen.TakeNote()}) → GameInput {_inputProbe?.Device ?? "-"} changes {_inputProbe?.Changes ?? 0}" +
                            (_inputProbe?.LastError is { Length: > 0 } pe ? $" ({pe})" : "") + $"; {engine}" + _delay.Take());
                        _maxGameBody = _maxGameTrig = 0;
                        var foreign = FindForeignPads();
                        if (foreign != ForeignPads)
                            DiagnosticsService.Info("Virtual pad (strict): other pads on XInput: " + (foreign ?? "none") + " (" + SlotTable() + ").");
                        ForeignPads = foreign;
                    }
                }
                // direct mode: wake on the pad's next report (a press goes out at once), else every 8 ms
                if (_direct) WinUsbGipTransport.Shared.InputArrived.WaitOne(8); else Thread.Sleep(8);
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private int _lastGoodSlot = -1;

    /// <summary>Our virtual pad's XInput slot and how far its packet number moved since the first look (a moving packet
    /// = the state we submit reaches XInput). HIDMaestro's Series BT / 360 pads are on XInput; the PS4 pad is not.</summary>
    private string VirtualXInputNote()
    {
        if (_virtualXSlot == -2)
        {
            _virtualXSlot = -1;
            for (int i = 0; i < 4; i++)
                if (i != _xinputSlot && IsOurs(i) && XInput.GetState((uint)i, out var st) == 0) { _virtualXSlot = i; _virtualXPacket0 = st.dwPacketNumber; break; }
            VirtualSlot = _virtualXSlot;
        }
        if (_virtualXSlot < 0) return "yok";
        return XInput.GetState((uint)_virtualXSlot, out var s) == 0 ? $"slot {_virtualXSlot} packets +{s.dwPacketNumber - _virtualXPacket0}" : $"slot {_virtualXSlot} gone";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CapsEx { public byte Type, SubType; public ushort Flags, Buttons; public byte LT, RT; public short LX, LY, RX, RY; public ushort WL, WR, Vid, Pid, Version, Unk1; public uint Unk2; }
    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint one, uint user, uint flags, out CapsEx caps);

    /// <summary>HIDMaestro's Xbox pads (360 045E:028E and the selectable One/Series/Elite types, OutputMatrix.SeriesPadTypes).
    /// Not 02FF: that is the Wolverine itself in Xbox mode (its GIP XInput child, 2026-11-22). After a replug the
    /// physical pad's slot is gone for a moment and the scan found the virtual pad (2026-11-15, "back on XInput slot 1").</summary>
    private static bool IsVirtualPad(int slot)
    {
        try { return XInputGetCapabilitiesEx(1, (uint)slot, 0, out var c) == 0 && c.Vid == 0x045E &&
                     c.Pid is 0x028E or 0x0B13 or 0x0B12 or 0x02EA or 0x02FD or 0x0B00 or 0x0B05; }
        catch { return false; }
    }

    private static (ushort Vid, ushort Pid) IdsOf(int slot)
    {
        try { return XInputGetCapabilitiesEx(1, (uint)slot, 0, out var c) == 0 ? (c.Vid, c.Pid) : ((ushort)0, (ushort)0); }
        catch { return (0, 0); }
    }

    /// <summary>"slot 0 045E:0B13, slot 1 045E:02FF" for every connected XInput slot.</summary>
    internal static string SlotTable()
    {
        var parts = new List<string>();
        for (int i = 0; i < 4; i++)
            if (XInput.GetState((uint)i, out _) == 0) { var (v, p) = IdsOf(i); parts.Add($"slot {i} {v:X4}:{p:X4}"); }
        return parts.Count == 0 ? "no XInput pad" : string.Join(", ", parts);
    }

    /// <summary>Other pads on XInput while the strict virtual pad runs (not the Wolverine, not our pad), e.g. a paired
    /// Bluetooth Xbox controller: a game may bind to it instead of the virtual pad. Null when none.</summary>
    public static volatile string? ForeignPads;

    private string? FindForeignPads()
    {
        var parts = new List<string>();
        for (int i = 0; i < 4; i++)
            if (i != _xinputSlot && XInput.GetState((uint)i, out _) == 0 && !IsOurs(i)) { var (v, p) = IdsOf(i); parts.Add($"{v:X4}:{p:X4}"); }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Our own virtual pad's XInput slot: it appeared after the pad was created (else, before that is known, the
    /// HIDMaestro VID/PID).</summary>
    private bool IsOurs(int slot) => _virtualXSlot >= 0 ? slot == _virtualXSlot
        : _slotsBefore >= 0 && _running ? (_slotsBefore & (1 << slot)) == 0 && slot != _lastGoodSlot
        : IsVirtualPad(slot);

    private bool TryReadXInput(out XInputPad pad)
    {
        pad = default;
        if (_direct)
        {
            // direct mode: the pad's own GIP pipe; after a replug reopen it (at most every second)
            var t = WinUsbGipTransport.Shared;
            if (!t.IsOpen && Environment.TickCount64 >= _nextDirectOpen) { _nextDirectOpen = Environment.TickCount64 + 1000; t.TryOpen(); }
            return t.TryReadPad(out pad);
        }
        int slot = _xinputSlot;
        if (slot < 0)
        {
            // The pad's earlier slot first (after a replug Windows usually gives it back, and the virtual Xbox pad may now
            // also be on XInput); otherwise a single connected slot. Two unknown pads: keep waiting, never guess.
            // Our own virtual pad is on XInput too: never read it back (checked only when a slot is chosen, not per loop).
            if (_lastGoodSlot >= 0 && XInput.GetState((uint)_lastGoodSlot, out _) == 0 && !IsOurs(_lastGoodSlot) &&
                IdsOf(_lastGoodSlot) is var (lv, lp) && XInputWolverineSlot.CanBe(lv, lp)) slot = _lastGoodSlot;
            else
            {
                // Another pad too (e.g. a paired Bluetooth Xbox controller) is never read as the Wolverine.
                var candidates = new List<(int, ushort, ushort)>();
                for (int i = 0; i < 4; i++)
                    if (XInput.GetState((uint)i, out _) == 0 && !IsOurs(i)) { var (v, p) = IdsOf(i); candidates.Add((i, v, p)); }
                slot = XInputWolverineSlot.Choose(candidates);
                if (slot < 0) return false;
            }
        }
        if (slot < 0 || XInput.GetState((uint)slot, out var s) != 0) { _xinputSlot = -1; return false; }
        _lastGoodSlot = slot;
        _xinputSlot = slot;
        var g = s.Gamepad;
        pad = new(g.wButtons, g.bLeftTrigger, g.bRightTrigger, g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY);
        return true;
    }

    private void Fail(string reason)
    {
        _failure = reason;
        _running = false;
        DiagnosticsService.Info("Virtual Xbox pad stopped: " + reason);
        try { _output.Stop(); } catch (Exception ex) { DiagnosticsService.Error("Xbox birleşik motor duruşu", ex); }
    }

    public void Dispose()
    {
        _running = false;
        ForeignPads = null;
        VirtualSlot = -1;
        try { _probeThread?.Join(500); _inputProbe?.Dispose(); } catch { }
        _watchdog?.Dispose();
        _watchdog = null;
        try
        {
            _output.Stop();
            _audio?.Dispose();
            _audio = null;
#if HAS_HIDMAESTRO
            if (_controller != null) _controller.OutputReceived -= OnOutput;
            var worker = _worker;
            var controller = _controller;
            var context = _context;
            _controller = null;
            _context = null;
            if (worker?.Join(1000) == false)
            {
                // Let an in-flight SDK call finish before releasing its controller.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    worker.Join();
                    try
                    {
                        try { controller?.Dispose(); }
                        finally { context?.Dispose(); }
                    }
                    catch (Exception ex) { DiagnosticsService.Error("Sanal Xbox aygıtı kapanışı", ex); }
                });
            }
            else
            {
                try { controller?.Dispose(); }
                finally { context?.Dispose(); }
            }
#else
            _worker?.Join(1000);
#endif
        }
        finally
        {
            try { _physical.Dispose(); }
            finally
            {
                if (_hideOwned) Interlocked.Decrement(ref s_hidersAlive);
                if (_hideOwned && HidHideControl.RestorePending) HidHideControl.Restore();
                _hideOwned = false;
            }
        }
    }
}
