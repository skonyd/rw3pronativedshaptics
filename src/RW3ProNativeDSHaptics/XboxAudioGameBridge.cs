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

    public XboxAudioGameBridge() => _output = new(frame =>
        _physical.Set(frame.BodyLeft, frame.BodyRight, frame.TriggerLeft, frame.TriggerRight));

    public bool IsRunning => _running && _audio?.IsCapturing == true;
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
        if (!synthetic && (!HidHideControl.Query().Ready || HidHideControl.RestorePending))
            throw new InvalidOperationException("HidHide hazır olmalı ve önceki gizleme geri alınmalı.");
        try
        {
            if (!_physical.TryAcquire() || !_physical.TryReadPad(out _))
                throw new InvalidOperationException("Xbox modunda tek Wolverine gerekli. " + _physical.LastError);
            if (!synthetic)
            {
#if HAS_HIDMAESTRO
                _context = new HMContext();
                _context.LoadDefaultProfiles();
                if (!_context.IsDriverInstalled) throw new InvalidOperationException("HIDMaestro sürücüsü hazır değil.");
                var model = _context.GetProfile("xbox-series-xs-bt") ??
                    throw new InvalidOperationException("Sanal Xbox Series profili bulunamadı.");
                _controller = _context.CreateController(model, "rw3-xbox-audio");
                _controller.OutputReceived += OnOutput;
                _hideOwned = true;
                HidHideControl.HidePad(null, xboxOnly: true);
                if (!HidHideControl.RestorePending || !_physical.TryReadPad(out _))
                    throw new InvalidOperationException("Gizleme sonrası Wolverine giriş yolu doğrulanamadı.");
#else
                throw new InvalidOperationException("HIDMaestro SDK bu derlemede yok.");
#endif
            }
            _audio = new AudioHapticsBridge(new XboxAudioSink(v =>
            {
                lock (_gate)
                {
                    _sound = synthetic
                        ? new(Math.Min(v.LeftMotor, .40), Math.Min(v.RightMotor, .40), 0, 0)
                        : new(v.LeftMotor, v.RightMotor, 0, 0);
                    _soundAt = Environment.TickCount64;
                }
            })) { Profile = profile, DelayOverrideMs = synthetic ? 0 : null };
            _audio.Start();
            _running = true;
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
        if (packet.Source == HMOutputSource.HidOutput) AcceptReport(packet.ReportId, packet.Data.Span);
    }
#endif

    internal void SubmitSyntheticReport(ReadOnlySpan<byte> data)
    {
#if HAS_HIDMAESTRO
        if (_controller != null) throw new InvalidOperationException("Sentetik rapor yalnız test yolunda kullanılabilir.");
#endif
        AcceptReport(0x0F, data);
    }

    private void AcceptReport(byte reportId, ReadOnlySpan<byte> data)
    {
        if (!_running || !XboxGameFeedback.TryDecode(reportId, data, out var frame)) return;
        lock (_gate)
        {
            if (!_running) return;
            _game = frame;
            _gameAt = Environment.TickCount64;
            Interlocked.Increment(ref _reports);
        }
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
            while (_running)
            {
                if (_audio?.IsCapturing != true)
                    throw new InvalidOperationException(_audio?.Failure ?? "Ses yakalama durdu.");
                if (!_physical.TryReadPad(out var pad))
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
                Thread.Sleep(8);
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string reason)
    {
        _failure = reason;
        _running = false;
        try { _output.Stop(); } catch (Exception ex) { DiagnosticsService.Error("Xbox birleşik motor duruşu", ex); }
    }

    public void Dispose()
    {
        _running = false;
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
                if (_hideOwned && HidHideControl.RestorePending) HidHideControl.Restore();
                _hideOwned = false;
            }
        }
    }
}
