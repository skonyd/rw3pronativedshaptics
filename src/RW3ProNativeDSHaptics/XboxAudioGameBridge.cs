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
    /// <summary>Main page levels for the game's rumble (0..100 %) and trigger motors (0..200 %), applied live.</summary>
    public static volatile int RumblePercent = 100, TriggerPercent = 100;
    /// <summary>Strict path: which pad the game sees (HIDMaestro profile, <see cref="OutputMatrix.Profile"/>).</summary>
    public string VirtualProfile { get; init; } = "xbox-360-wired";
    private int _xinputSlot = -1;
    /// <summary>Virtual Xbox/PS4 pads of this process that currently own a HidHide state.</summary>
    private static int s_hidersAlive;
    /// <summary>Strict path: the Sensa engine's state for the periodic log (set by the orchestrator).</summary>
    public Func<string>? EngineProbe { get; init; }
    private double _maxGameBody, _maxGameTrig;
    // Input path diagnostics (strict): physical read changes → virtual pad on XInput → GameInput reading (as a GDK game).
    private long _physChanges;
    private XInputPad _lastPad;
    private VirtualPadInputProbe? _inputProbe;
    private Thread? _probeThread;
    private int _virtualXSlot = -2;
    private uint _virtualXPacket0;
    private long _xinputWrites;
    private ushort _bodyL, _bodyR;
    private long _bodyTick;

    public XboxAudioGameBridge() => _output = new(frame =>
    {
        if (!Strict) { _physical.Set(frame.BodyLeft, frame.BodyRight, frame.TriggerLeft, frame.TriggerRight); return; }
        double rumble = Math.Clamp(RumblePercent, 0, 100) / 100.0;
        ushort l = (ushort)(BodyMotorLimit.Big(frame.BodyLeft * rumble) * 65535), r = (ushort)(Math.Clamp(frame.BodyRight * rumble, 0, 1) * 65535);
        long now = Environment.TickCount64;
        // Only on change (or every 100 ms as a keep-alive): the loop runs every 8 ms.
        if (_xinputSlot >= 0 && (l != _bodyL || r != _bodyR || (l | r) != 0 && now - _bodyTick >= 100))
        {
            var v = new XInput.Vibration { wLeftMotorSpeed = l, wRightMotorSpeed = r };
            if (XInput.XInputSetState((uint)_xinputSlot, ref v) == 0) { _bodyL = l; _bodyR = r; _bodyTick = now; _xinputWrites++; }
        }
        XboxSensa.SetTriggers(XboxNativeMix.TriggerGain(frame.TriggerLeft, TriggerPercent), XboxNativeMix.TriggerGain(frame.TriggerRight, TriggerPercent));
    });

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
            if (Strict) { if (!TryReadXInput(out _)) throw new InvalidOperationException("Wolverine XInput ile görünmüyor (tek kol bağlı olmalı)."); }
            else if (!_physical.TryAcquire() || !_physical.TryReadPad(out _))
                throw new InvalidOperationException("Xbox modunda tek Wolverine gerekli. " + _physical.LastError);
            if (!synthetic)
            {
#if HAS_HIDMAESTRO
                _context = new HMContext();
                _context.LoadDefaultProfiles();
                if (!_context.IsDriverInstalled) throw new InvalidOperationException("HIDMaestro sürücüsü hazır değil.");
                // Strict: the output the user picked (Xbox 360 / Series / PS4). Banishers did not take the Bluetooth Series
                // profile (a HID device) as a gamepad through GameInput: it sent rumble to it but ignored its input
                // (E-20261115-02); the Xbox 360 pad (XUSB) is the safe choice there.
                string profileId = Strict ? VirtualProfile : "xbox-series-xs-bt";
                var model = _context.GetProfile(profileId) ??
                    throw new InvalidOperationException("Sanal kol profili bulunamadı: " + profileId + ".");
                _controller = _context.CreateController(model, "rw3-xbox-audio");
                _controller.OutputReceived += OnOutput;
                _hideOwned = true;
                Interlocked.Increment(ref s_hidersAlive);
                // Strict also runs in PC mode (the pad is XInput there too): hide whichever nodes the pad has now.
                HidHideControl.HidePad(null, xboxOnly: !Strict || WolverineModeControl.Detect() == WolverineControllerMode.Xbox);
                if (Strict)
                    try { DiagnosticsService.Info("Virtual Xbox pad (strict): " + HidHideControl.DisallowApp(HidHideControl.GameInputServicePath)); }
                    catch (Exception ex) { DiagnosticsService.Info("Virtual Xbox pad (strict): GameInputSvc " + ex.Message); }
                if (!HidHideControl.RestorePending || !(Strict ? TryReadXInput(out _) : _physical.TryReadPad(out _)))
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
                    if (_output.CheckTimeout(Environment.TickCount64))
                        Fail("Xbox giriş watchdog'u motorları durdurdu.");
                }
                catch (Exception ex) { Fail(ex.Message); }
            }, null, 25, 25);
            _worker = new Thread(Run) { IsBackground = true, Name = "RW3 Xbox game feedback" };
            _worker.Start();
        }
        catch { Dispose(); throw; }
    }

#if HAS_HIDMAESTRO
    private void OnOutput(HMController controller, HMOutputPacket packet)
    {
        if (packet.Source == HMOutputSource.HidOutput && AcceptReport(packet.ReportId, packet.Data.Span)) return;
        if (packet.Source == HMOutputSource.HidOutput && TryDecodeDualShock4(packet.ReportId, packet.Data.Span, out var ds4))
        {
            lock (_gate) { if (!_running) return; _game = ds4; _gameAt = Environment.TickCount64; Interlocked.Increment(ref _reports); }
            return;
        }
        // Not a Series 0x0F report (the Xbox 360 profile's rumble arrives another way): log the first few shapes.
        if (Interlocked.Increment(ref _unknownLogged) <= 6)
            DiagnosticsService.Info($"Virtual Xbox pad: unhandled output {packet.Source} id 0x{packet.ReportId:X2} len {packet.Data.Length}: " +
                Convert.ToHexString(packet.Data.Span[..Math.Min(16, packet.Data.Length)]));
        var d = packet.Data.Span;
        // XUSB set-state as the Xbox 360 profile may hand it over: [.., left, right] motors 0..255 at the end of an 8-byte
        // 00 08 00 L R 00 00 00 report (the XInput wire format).
        if (Strict && d.Length >= 5 && d[0] == 0x00 && d[1] == 0x08)
            lock (_gate) { _game = new(d[3] / 255.0, d[4] / 255.0, 0, 0); _gameAt = Environment.TickCount64; Interlocked.Increment(ref _reports); }
    }
    private int _unknownLogged;

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
            if (Strict && _inputProbe == null)
            {
                // Own thread: GameInput's first enumeration blocks for a while, and the motor watchdog stops the bridge
                // when this loop pauses > 250 ms (2026-11-15).
                _inputProbe = new VirtualPadInputProbe();
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
                    // A replug (which the strict setup asks for) drops XInput for a few seconds. Until 2026-11-15 that ended
                    // the bridge silently, HidHide was restored and the game went back to the physical pad.
                    if (!TryReadXInput(out pad))
                    {
                        long t = Environment.TickCount64;
                        if (lostSince == 0) { lostSince = t; DiagnosticsService.Info("Virtual Xbox pad (strict): Wolverine left XInput; waiting up to 60 s."); }
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
                }
#endif
                long now = Environment.TickCount64;
                BridgeMotorFrame frame;
                lock (_gate)
                    frame = XboxGameFeedback.Mix(!_muteTestAudio && now - _soundAt < 250 ? _sound : default,
                        now - _gameAt < 1000 ? _game : default, _includeGameRumble, _includeGameTriggers);
                if (_running) _output.Submit(frame, now);
                if (Strict)
                {
                    if (!pad.Equals(_lastPad)) { _physChanges++; _lastPad = pad; }
                    _maxGameBody = Math.Max(_maxGameBody, Math.Max(_game.BodyLeft, _game.BodyRight));
                    _maxGameTrig = Math.Max(_maxGameTrig, Math.Max(_game.TriggerLeft, _game.TriggerRight));
                    if (now >= nextLog)
                    {
                        nextLog = now + 10_000;
                        string engine = ""; try { engine = EngineProbe?.Invoke() ?? ""; } catch { }
                        DiagnosticsService.Info($"Virtual {VirtualProfile} (strict): game reports {FeedbackReports}, game max body {_maxGameBody:F2} trig {_maxGameTrig:F2}, " +
                            $"XInput writes {_xinputWrites} (slot {_xinputSlot}), foreground takes pad {ControllerFocusGuard.ForegroundTakesController()}; " +
                            $"INPUT physical changes {_physChanges} → virtual XInput {VirtualXInputNote()} → GameInput {_inputProbe?.Device ?? "-"} changes {_inputProbe?.Changes ?? 0}" +
                            (_inputProbe?.LastError is { Length: > 0 } pe ? $" ({pe})" : "") + $"; {engine}");
                        _maxGameBody = _maxGameTrig = 0;
                    }
                }
                Thread.Sleep(8);
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
                if (i != _xinputSlot && IsVirtualPad(i) && XInput.GetState((uint)i, out var st) == 0) { _virtualXSlot = i; _virtualXPacket0 = st.dwPacketNumber; break; }
        }
        if (_virtualXSlot < 0) return "yok";
        return XInput.GetState((uint)_virtualXSlot, out var s) == 0 ? $"slot {_virtualXSlot} packets +{s.dwPacketNumber - _virtualXPacket0}" : $"slot {_virtualXSlot} gone";
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CapsEx { public byte Type, SubType; public ushort Flags, Buttons; public byte LT, RT; public short LX, LY, RX, RY; public ushort WL, WR, Vid, Pid, Version, Unk1; public uint Unk2; }
    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint one, uint user, uint flags, out CapsEx caps);

    /// <summary>HIDMaestro's Xbox 360 (045E:028E) and Series BT (045E:0B13) pads. After a replug the physical pad's slot
    /// is gone for a moment and the scan found the virtual pad (2026-11-15, "back on XInput slot 1").</summary>
    private static bool IsVirtualPad(int slot)
    {
        try { return XInputGetCapabilitiesEx(1, (uint)slot, 0, out var c) == 0 && c.Vid == 0x045E && c.Pid is 0x028E or 0x0B13; }
        catch { return false; }
    }

    private bool TryReadXInput(out XInputPad pad)
    {
        pad = default;
        int slot = _xinputSlot;
        if (slot < 0)
        {
            // The pad's earlier slot first (after a replug Windows usually gives it back, and the virtual Xbox pad may now
            // also be on XInput); otherwise a single connected slot. Two unknown pads: keep waiting, never guess.
            // Our own virtual pad is on XInput too: never read it back (checked only when a slot is chosen, not per loop).
            if (_lastGoodSlot >= 0 && XInput.GetState((uint)_lastGoodSlot, out _) == 0 && !IsVirtualPad(_lastGoodSlot)) slot = _lastGoodSlot;
            else
                for (int i = 0; i < 4; i++)
                    if (XInput.GetState((uint)i, out _) == 0 && !IsVirtualPad(i)) { if (slot >= 0) return false; slot = i; }
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
