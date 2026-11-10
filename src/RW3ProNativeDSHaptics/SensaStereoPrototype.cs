using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>One Interhaptics body target (GroupID + LateralFlag) used for a Wolverine side.</summary>
public sealed record SensaTarget(string Label, int Group, int Side)
{
    public string Key => Group + ":" + Side;
}

/// <summary>
/// v2 Native Sensa engine (public Interhaptics engine + Razer provider).
/// At the current 33 ms output cadence it streams, per actuator, an amplitude curve (4 points), one frequency
/// (30-400 Hz, Wolverine resonance 50 Hz) and up to 2 transients via UpdateParametricEffect,
/// the fields the Wolverine stream ("Sage T1": 3 bands / 4 points / 2 transients) carries.
/// Measured 2026-11-07: an updated effect appears in the very next frame; left/right are
/// separate frame blocks. Still a parametric encoding, NOT the 48 kHz DualSense waveform.
/// Windows x64 only; vendor DLLs are NEVER bundled or downloaded by this project.
/// Based on official Interhaptics shared types, plus independently documented
/// Razer engine lifecycle: https://padforge.org/docs/reference/sensa-haptics-internals/
/// </summary>
public sealed class SensaStereoPrototype : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CommandData
    {
        public readonly int Sign, Group, Side;
        public CommandData(int group, int side) { Sign = 1; Group = group; Side = side; }
    }

    private static class Engine
    {
        private const string Dll = "HAR";
        [DllImport(Dll)] public static extern bool Init();
        [DllImport(Dll)] public static extern void Quit();
        [DllImport(Dll)]
        public static extern int AddParametricEffect(
            [In] double[] amplitude, int amplitudeSize,
            [In] double[]? pitch, int pitchSize, double minHz, double maxHz,
            [In] double[]? transient, int transientSize, bool loop);
        [DllImport(Dll)]
        public static extern bool UpdateParametricEffect(int id,
            [In] double[] amplitude, int amplitudeSize,
            [In] double[]? pitch, int pitchSize, double minHz, double maxHz,
            [In] double[]? transient, int transientSize, bool loop);
        [DllImport(Dll)] public static extern int AddHM([MarshalAs(UnmanagedType.LPStr)] string content);
        [DllImport(Dll)] public static extern bool UpdateHM(int id, [MarshalAs(UnmanagedType.LPStr)] string content);
        [DllImport(Dll)] public static extern void AddTargetToEventMarshal(
            int effectId, [In] CommandData[] targets, int count);
        [DllImport(Dll)] public static extern void SetEventIntensity(int id, double intensity);
        [DllImport(Dll)] public static extern void PlayEvent(int id, double vibrationOffset,
            double textureOffset, double stiffnessOffset);
        [DllImport(Dll)] public static extern void ComputeAllEvents(double currentSeconds);
        [DllImport(Dll)] public static extern void StopAllEvents();
        /// <summary>Stops one event (public Interhaptics API: StopEvent(int hMaterialId)).</summary>
        [DllImport(Dll)] public static extern void StopEvent(int materialId);
        // Official SDK: looping is configured per event (HapticSource.ApplyLooping).
        [DllImport(Dll)] public static extern void SetEventLoop(int id, int loopValue);
    }

    // PlayEvent plays an effect ONCE from "now" (official SDK: offset = -currentTime).
    // v2 rewrites each side's short curve every tick and restarts it, so no effect can
    // expire mid-stream (the 1 s envelope bug of 2026-11-07).

    // Wolverine metadata (RzInterHaptics, "Sage T1"): VCM 30-400 Hz, resonance 50 Hz.
    public const double FrequencyMinHz = 30, FrequencyMaxHz = 400, ResonanceHz = 50;
    /// <summary>Frequency used for test pulses and when the source frequency is unknown.</summary>
    public const double TestFrequencyHz = 60;
    /// <summary>Curve hold after the tick window; covers a late next tick without a gap.</summary>
    private const double HoldSeconds = 0.030;
    /// <summary>Output fades to silence when no new source data arrives for this long.</summary>
    private const double SourceTimeoutSeconds = 0.060;

    /// <summary>Feature source for the live bridge (DualSense rear channels).</summary>
    public HapticFeatureExtractor Features { get; } = new();
    /// <summary>
    /// How frames reach the controller. Synapse (default): licensed HAR + Razer provider → Synapse →
    /// USB. DirectUsb: our own frame encoder (SensaFrameEncoder) written straight to the Wolverine's
    /// HID control collection (SensaHidTransport) — no Synapse, no HAR, no admin. Set while stopped.
    /// </summary>
    public SensaTransport Transport
    {
        get => (SensaTransport)Volatile.Read(ref _transport);
        set { lock (_lifecycle) {
            if (_worker?.IsAlive == true) throw new InvalidOperationException("Stop the engine before changing its transport.");
            Volatile.Write(ref _transport, (int)value);
        } }
    }
    private int _transport;
    public long DirectFramesSent { get; private set; }
    public string? DirectDevicePath { get; private set; }

    /// <summary>Output method; set only while stopped. Multiband/Hybrid are experimental, no phase control.</summary>
    private int _outputMethod;
    public SensaOutputMethod OutputMethod
    {
        get => (SensaOutputMethod)Volatile.Read(ref _outputMethod);
        set { lock (_lifecycle) {
            if (_worker?.IsAlive == true) throw new InvalidOperationException("Stop the engine before changing its output backend.");
            Volatile.Write(ref _outputMethod, (int)value);
        } }
    }
    /// <summary>Compatibility switch: true = <see cref="SensaOutputMethod.Multiband"/>.</summary>
    public bool ExperimentalMultiband
    {
        get => OutputMethod == SensaOutputMethod.Multiband;
        set => OutputMethod = value ? SensaOutputMethod.Multiband : SensaOutputMethod.Standard;
    }
    /// <summary>Hybrid: frames per hand sent as multiband / as standard (diagnostics).</summary>
    public long HybridMultiFrames { get; private set; }
    public long HybridStandardFrames { get; private set; }
    public long MultibandFrames { get; private set; }
    public long MultibandHeadroomReductions { get; private set; }

    private int _sourceGainPercent = 140;
    /// <summary>Source envelope to Sensa amplitude gain in percent (Wolverine only).</summary>
    public int SourceGainPercent
    {
        get => Volatile.Read(ref _sourceGainPercent);
        set => Volatile.Write(ref _sourceGainPercent, Math.Clamp(value, 10, 1200));   // × audio sensitivity; HapticLimiter keeps it bounded
    }

    private long _tickCount, _lastRateTicks, _lastRateStamp;
    private int _measuredTickHzBits;
    /// <summary>Measured engine tick rate (Hz), updated about once per second.</summary>
    public float MeasuredTickHz => BitConverter.Int32BitsToSingle(Volatile.Read(ref _measuredTickHzBits));
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Kept for v1 callers; v2 restarts both curves every tick anyway.</summary>
    public void RequestRetrigger() { }

    private static class Timing
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
        [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        public const uint HighResolution = 0x2, AllAccess = 0x1F0003;
    }

    private static class Provider
    {
        private const string Dll = "Interhaptics.RazerProvider";
        [DllImport(Dll)] public static extern bool ProviderInit();
        [DllImport(Dll)] public static extern bool ProviderIsPresent();
        [DllImport(Dll)] public static extern void ProviderRenderHaptics();
        [DllImport(Dll)] public static extern bool ProviderClean();
        // Same frame construction as ProviderRenderHaptics, also delivered to the device,
        // but copies frame <index> into the caller buffer. Argument order measured 2026-11-07.
        [DllImport(Dll)] public static extern int RenderHapticsDebug([Out] byte[] buffer, int index, int size);
    }

    /// <summary>
    /// Diagnostics: when true the engine renders through RenderHapticsDebug and keeps a copy
    /// of the last frame sent toward the Wolverine (41 B; 44/47 B with transients).
    /// </summary>
    public bool CaptureFrames { get; set; }
    private readonly byte[] _frameScratch = new byte[256];
    private byte[] _lastFrame = Array.Empty<byte>();
    private long _transientFrames;
    public byte[] LastFrame { get { lock (_lock) return (byte[])_lastFrame.Clone(); } }
    public long TransientFrames => Interlocked.Read(ref _transientFrames);

    private Thread? _worker;
    private int _stop, _leftAmplitudeBits, _rightAmplitudeBits;
    private int _outputLevelPercent = 100;
    // Safe user intensity applies to Wolverine parametric events ONLY.
    // It must never modify the USB DualSense's original PCM frames.
    public int OutputLevelPercent
    {
        get => Volatile.Read(ref _outputLevelPercent);
        set => Volatile.Write(ref _outputLevelPercent, HapticOutputLevel.Limit(value));
    }
    private long _lastSignalTick;
    private int _providerActive, _renderTicks;
    private string _status = "Stopped";
    private readonly object _lock = new();
    private readonly object _lifecycle = new();

    public bool IsActive => Volatile.Read(ref _providerActive) == 1;
    public int EngineTicks => Volatile.Read(ref _renderTicks);
    public string Status { get { lock (_lock) return _status; } }
    public bool IsRunning => _worker?.IsAlive == true;

    // Shared types (official): GroupID.Hand=302, LateralFlag.Left=2, Right=1.
    // Razer's own Synesthesia configs (RazerSensa_DevKit, e.g. ZIPLINE_ON_Left/Right)
    // spatialize controller effects as Target "Hand" + Left/Right. The earlier Palm(400)
    // target reached only the left actuator weakly on a real Wolverine V3 Pro (2026-11-07).
    public const int HandGroup = 302;

    // Which body targets drive the two Wolverine actuators (measured: Hand L/R reach
    // Left_palm 216 / Right_palm 116 as separate frame blocks).
    private static SensaTarget _leftTarget = new("Hand • Sol", HandGroup, LeftSide);
    private static SensaTarget _rightTarget = new("Hand • Sağ", HandGroup, RightSide);
    public static SensaTarget LeftTarget
    {
        get => Volatile.Read(ref _leftTarget);
        set => Volatile.Write(ref _leftTarget, value);
    }
    public static SensaTarget RightTarget
    {
        get => Volatile.Read(ref _rightTarget);
        set => Volatile.Write(ref _rightTarget, value);
    }


    public const int LeftSide = 2, RightSide = 1;
    /// <summary>Engine tick. v1 used Thread.Sleep(20), about 31 ms on the default Windows timer.</summary>
    /// <summary>
    /// Frame period. Measured 2026-11-07 with the webcam mic: Synapse plays every rendered frame as
    /// a fixed-length chunk of roughly 30-37 ms, whatever the call rate. At 5 ms ticks frames
    /// queued up 7x faster than they played: a 2 s pulse vibrated for 14.7 s, the 6 s audio test
    /// for 30-50 s, and vibration drifted further behind the sound. 25 ms still stretched x1.26;
    /// 33/36/40 ms ended within 0.2-0.25 s of the drive even after 15 s of continuous output.
    /// </summary>
    public static int TickMs = 33;
    /// <summary>Ceiling for manual Submit() test amplitudes.</summary>
    public const float MaxBridgeAmplitude = 1.0f;
    // Side test pulse. 0.20 x 385ms and 0.38 x 800ms were both reported as weak on a real
    // Wolverine V3 Pro; a 1 s full-scale pulse (still scaled by the user's output level)
    // is used for short, user-initiated tests only.
    public const float TestPulseAmplitude = 1.0f;

    private void Report(string status)
    {
        lock (_lock) _status = status;
    }

    public bool Start()
    {
        lock (_lifecycle)
        {
            // Never re-initialize a process-global HAR engine while the old worker
            // may still be rendering or cleaning up native resources.
            if (_worker?.IsAlive == true)
                return Volatile.Read(ref _stop) == 0;
            _worker = null;
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Report("Only Windows x64 with official Interhaptics binaries is supported.");
                return false;
            }
            string har = Path.Combine(AppContext.BaseDirectory, "HAR.dll");
            string provider = Path.Combine(AppContext.BaseDirectory, "Interhaptics.RazerProvider.dll");
            if (Transport == SensaTransport.Synapse && (!File.Exists(har) || !File.Exists(provider)))
            {
                Report("Official HAR.dll and Interhaptics.RazerProvider.dll not found beside app. " +
                    "Obtain them from the licensed WYVRN SDK, then restart.");
                return false;
            }
            Volatile.Write(ref _stop, 0);
            Volatile.Write(ref _providerActive, 0);
            Volatile.Write(ref _renderTicks, 0);
            Features.EnableSpectralAnalysis(OutputMethod != SensaOutputMethod.Standard);
            DirectFramesSent = 0;
            MultibandFrames = MultibandHeadroomReductions = HybridMultiFrames = HybridStandardFrames = 0;
            _worker = new Thread(Transport == SensaTransport.DirectUsb ? RunDirect : Run) { IsBackground = true, Name = "RW3-SensaStereo" };
            _worker.Start();
            return true;
        }
    }

    public void Submit(float left, float right)
    {
        SubmitClamped(left, right, MaxBridgeAmplitude);
    }

    private void SubmitClamped(float left, float right, float max)
    {
        // P/Invoke must never receive NaN/Infinity from user or source data.
        left = float.IsFinite(left) ? Math.Clamp(left, 0, max) : 0;
        right = float.IsFinite(right) ? Math.Clamp(right, 0, max) : 0;
        Volatile.Write(ref _leftAmplitudeBits, BitConverter.SingleToInt32Bits(left));
        Volatile.Write(ref _rightAmplitudeBits, BitConverter.SingleToInt32Bits(right));
        Interlocked.Exchange(ref _lastSignalTick, Environment.TickCount64);
    }

    /// <summary>
    /// Direct USB loop: same feature path, gain, limiter, pitch hold and 33 ms cadence as the Synapse
    /// path, but frames are encoded here and written to the controller. Transients are folded into the
    /// nearest strength point (the transient record format is not decoded yet). After sound stops, two
    /// silence frames are sent (the controller keeps playing the last frame otherwise), then nothing,
    /// like Synapse. Input is polled every 5 ms while silent so the first frame of a sound goes out at once.
    /// </summary>
    private void RunDirect()
    {
        IntPtr timer = IntPtr.Zero, mmcss = IntPtr.Zero;
        bool periodSet = false;
        var clock = Stopwatch.StartNew();
        using var tx = new SensaHidTransport();
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try { uint task = 0; mmcss = Timing.AvSetMmThreadCharacteristicsW("Games", ref task); } catch { }
            timer = Timing.CreateWaitableTimerExW(IntPtr.Zero, null, Timing.HighResolution, Timing.AllAccess);
            if (timer == IntPtr.Zero) periodSet = Timing.timeBeginPeriod(1) == 0;
            Report("Doğrudan USB: Wolverine aranıyor (PC modunda, kablo ya da alıcıyla)");
            double reopenAt = 0;
            var frame = new byte[SensaFrameEncoder.MaxFrameLength];
            byte[] silence = SensaFrameEncoder.Silence();
            var la = new double[4]; var ra = new double[4];
            var lHold = new PitchHold(); var rHold = new PitchHold();
            var method = OutputMethod;
            bool multibandMode = method == SensaOutputMethod.Multiband, hybridMode = method == SensaOutputMethod.Hybrid;
            var lPolicy = new HybridOutputPolicy(); var rPolicy = new HybridOutputPolicy();
            var lBands = new SensaBand[3]; var rBands = new SensaBand[3];
            long cursor = Features.CompletedSlots;
            SideFeatures lastL = default, lastR = default;
            double lastSourceAt = -1, tickSec = TickMs / 1000.0, next = clock.Elapsed.TotalSeconds;
            int silenceToSend = 0;
            bool wasActive = false;
            _lastRateStamp = Stopwatch.GetTimestamp(); _lastRateTicks = 0;
            while (Volatile.Read(ref _stop) == 0)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (!tx.IsOpen && now >= reopenAt)
                {
                    if (tx.Open())
                    {
                        DirectDevicePath = tx.DevicePath;
                        Volatile.Write(ref _providerActive, 1);
                        Report("Doğrudan USB çalışıyor • kendi kodlayıcı, Synapse gerekmez • " + TickMs + " ms");
                        silenceToSend = 2;
                    }
                    else { Volatile.Write(ref _providerActive, 0); reopenAt = now + 1.0; Report("Wolverine PC modunda bulunamadı (Xbox modunda Sensa yoktur); tekrar deneniyor"); }
                }
                SideFeatures fl, fr;
                double gain = SourceGainPercent / 100.0;
                bool manual = Environment.TickCount64 - Interlocked.Read(ref _lastSignalTick) <= 140;
                if (manual)
                {
                    fl = Constant(BitConverter.Int32BitsToSingle(Volatile.Read(ref _leftAmplitudeBits)));
                    fr = Constant(BitConverter.Int32BitsToSingle(Volatile.Read(ref _rightAmplitudeBits)));
                    gain = 1.0;
                    cursor = Features.CompletedSlots;
                }
                else
                {
                    int n = Features.Take(ref cursor, out fl, out fr);
                    if (n > 0) { lastL = fl; lastR = fr; lastSourceAt = now; }
                    else if (lastSourceAt >= 0 && now - lastSourceAt <= SourceTimeoutSeconds) { fl = Hold(lastL); fr = Hold(lastR); }
                    else { fl = default; fr = default; }
                }
                double level = HapticOutputLevel.Apply(gain, OutputLevelPercent);
                bool records = DirectTransientRecords && SendTransients && !manual;
                bool leftOn = DirectSensaMapping.Points(fl, level, la, records), rightOn = DirectSensaMapping.Points(fr, level, ra, records);
                bool active = leftOn || rightOn;
                if (active && !wasActive) next = now;      // first frame of a sound: no wait
                bool sendFrame = active || silenceToSend > 0;
                if (sendFrame && tx.IsOpen)
                {
                    bool ok;
                    if (active)
                    {
                        HapticSpectrum ls = default, rs = default;
                        bool haveSpectrum = (multibandMode || hybridMode) && !manual && Features.TryTakeSpectrum(out ls, out rs);
                        int ln = DirectBands(fl, la, ls, haveSpectrum, multibandMode, hybridMode, lPolicy, lHold, level, now, lBands);
                        int rn = DirectBands(fr, ra, rs, haveSpectrum, multibandMode, hybridMode, rPolicy, rHold, level, now, rBands);
                        int length = SensaFrameEncoder.EncodeBands(lBands.AsSpan(0, ln), rBands.AsSpan(0, rn), frame,
                            records ? DirectSensaMapping.Hit(fl, level) : null, records ? DirectSensaMapping.Hit(fr, level) : null);
                        ok = tx.Send(frame.AsSpan(0, length));
                        silenceToSend = 2;
                        LastSentHz = (lHold.Last, rHold.Last);
                    }
                    else { ok = tx.Send(silence); silenceToSend--; }
                    if (ok) { DirectFramesSent = tx.FramesSent; Interlocked.Increment(ref _renderTicks); }
                    else
                    {
                        Report("Direct USB write failed (error " + tx.LastError + "); reconnecting");
                        tx.Close(); Volatile.Write(ref _providerActive, 0); reopenAt = now + 1.0;
                    }
                }
                FrameProbe?.Invoke(fl, fr, level, !sendFrame);
                wasActive = active;
                UpdateRate();
                next += sendFrame ? tickSec : IdlePollSeconds;
                double wait = next - clock.Elapsed.TotalSeconds;
                if (wait < -0.050) { next = clock.Elapsed.TotalSeconds; continue; }
                if (wait > 0) WaitSeconds(timer, wait);
            }
            if (tx.IsOpen) for (int i = 0; i < 2; i++) { tx.Send(silence); Thread.Sleep(TickMs); }
        }
        catch (Exception ex)
        {
            Report("Direct USB error: " + ex.GetType().Name + " - " + ex.Message);
            DiagnosticsService.Error("Sensa direct USB", ex);
        }
        finally
        {
            Volatile.Write(ref _providerActive, 0);
            Volatile.Write(ref _leftAmplitudeBits, 0);
            Volatile.Write(ref _rightAmplitudeBits, 0);
            if (timer != IntPtr.Zero) Timing.CloseHandle(timer);
            if (periodSet) Timing.timeEndPeriod(1);
            if (mmcss != IntPtr.Zero) { try { Timing.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
        }
    }

    /// <summary>
    /// One hand's bands for the Direct USB frame. Standard: one band (fast envelope, folded transients,
    /// held pitch). Multiband: up to 3 spectral components, each shaped by the fast envelope.
    /// Hybrid: HybridOutputPolicy decides per frame; standard frames of a mixed sound use its
    /// strongest real component as pitch. Total strength across bands is kept ≤ 1.
    /// </summary>
    private int DirectBands(in SideFeatures f, double[] points, in HapticSpectrum spectrum, bool haveSpectrum,
        bool multibandMode, bool hybridMode, HybridOutputPolicy policy, PitchHold hold, double level, double now, SensaBand[] bands)
    {
        bool multi = multibandMode ? haveSpectrum && spectrum.Count > 0
                   : hybridMode && policy.Decide(f, spectrum, haveSpectrum, now);
        if (!multi)
        {
            var g = hybridMode ? HybridOutputPolicy.StandardFrame(f, spectrum, haveSpectrum) : f;
            bands[0] = new SensaBand(points[0], points[1], points[2], points[3], PitchFor(hold.Resolve(g.FrequencyHz, now)));
            if (hybridMode && f.Peak > HybridOutputPolicy.ActiveThreshold) HybridStandardFrames++;
            return 1;
        }
        var sp = hybridMode ? HybridOutputPolicy.ScaleToEnvelope(spectrum, f.Peak) : spectrum;
        double sum = sp.AmplitudeSum * level, scale = sum > 1 ? level / sum : level;
        double peak = f.Peak;
        double Ratio(float v) => peak > 1e-6 ? Math.Clamp(v / peak, 0, 1) : 0;
        int n = Math.Min(3, sp.Count);
        for (int i = 0; i < n; i++)
        {
            var c = sp[i];
            double a = c.Amplitude * scale;
            bands[i] = new SensaBand(a * Ratio(f.P0), a * Ratio(f.P1), a * Ratio(f.P2), a * Ratio(f.P3), PitchFor(c.FrequencyHz));
        }
        hold.Resolve(n > 0 ? sp[0].FrequencyHz : 0, now);
        if (hybridMode) HybridMultiFrames++; else MultibandFrames++;
        if (sum > 1) MultibandHeadroomReductions++;
        return Math.Max(1, n);
    }

    /// <summary>Per-hand pitch hold (same rule as the Synapse path: keep the last pitch for up to 1 s).</summary>
    private sealed class PitchHold
    {
        private double _hz, _at = double.NaN;
        public double Last => _hz;
        public double Resolve(double hz, double now)
        {
            if (hz > 0) { _hz = hz; _at = now; return hz; }
            return now - _at <= 1.0 ? _hz : 0;
        }
    }

    private void Run()
    {
        string nativeStage = "initialization";
        bool init = false, providerInit = false;
        IntPtr timer = IntPtr.Zero, mmcss = IntPtr.Zero;
        bool periodSet = false;
        var clock = Stopwatch.StartNew();
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try { uint task = 0; mmcss = Timing.AvSetMmThreadCharacteristicsW("Games", ref task); } catch { }
            timer = Timing.CreateWaitableTimerExW(IntPtr.Zero, null, Timing.HighResolution, Timing.AllAccess);
            if (timer == IntPtr.Zero) periodSet = Timing.timeBeginPeriod(1) == 0;

            Report("Starting HAR.dll engine");
            init = Engine.Init();
            if (!init) { Report("HAR.Init returned false"); return; }
            clock.Restart();
            double[] silent = { 0, 0, 1, 0 };
            var method = OutputMethod;
            bool multiband = method == SensaOutputMethod.Multiband, hybrid = method == SensaOutputMethod.Hybrid;
            string initialHaps = multiband || hybrid ? MultibandHaps.Build(default, default, 1, out _) : "";
            int left = multiband ? Engine.AddHM(initialHaps.Replace("RW3 experimental", "RW3 left experimental", StringComparison.Ordinal)) : Engine.AddParametricEffect(silent, 4, null, 0, FrequencyMinHz, FrequencyMaxHz, null, 0, false);
            int right = multiband ? Engine.AddHM(initialHaps.Replace("RW3 experimental", "RW3 right experimental", StringComparison.Ordinal)) : Engine.AddParametricEffect(silent, 4, null, 0, FrequencyMinHz, FrequencyMaxHz, null, 0, false);
            if (left < 0 || right < 0) { Report("Failed to create stereo parametric effects"); return; }
            if (left == right) throw new InvalidOperationException("Stereo effects received the same material ID.");
            SensaTarget leftTarget = LeftTarget, rightTarget = RightTarget;
            Engine.AddTargetToEventMarshal(left, new[] { new CommandData(leftTarget.Group, leftTarget.Side) }, 1);
            Engine.AddTargetToEventMarshal(right, new[] { new CommandData(rightTarget.Group, rightTarget.Side) }, 1);
            Engine.SetEventIntensity(left, 1);
            Engine.SetEventIntensity(right, 1);
            int leftHm = -1, rightHm = -1;
            if (hybrid)
            {
                // Hybrid keeps the parametric effects (standard frames) and adds one .haps effect per hand.
                leftHm = Engine.AddHM(initialHaps.Replace("RW3 experimental", "RW3 left hybrid", StringComparison.Ordinal));
                rightHm = Engine.AddHM(initialHaps.Replace("RW3 experimental", "RW3 right hybrid", StringComparison.Ordinal));
                if (leftHm < 0 || rightHm < 0 || leftHm == rightHm) { Report("Failed to create hybrid .haps effects"); return; }
                Engine.AddTargetToEventMarshal(leftHm, new[] { new CommandData(leftTarget.Group, leftTarget.Side) }, 1);
                Engine.AddTargetToEventMarshal(rightHm, new[] { new CommandData(rightTarget.Group, rightTarget.Side) }, 1);
                Engine.SetEventIntensity(leftHm, 1);
                Engine.SetEventIntensity(rightHm, 1);
            }
            var leftPolicy = new HybridOutputPolicy();
            var rightPolicy = new HybridOutputPolicy();

            Report("Interhaptics engine started; finding Razer runtime");
            providerInit = Provider.ProviderInit();
            if (!providerInit) { Report("Razer runtime unavailable; install Synapse 4 + Chroma + PC mode"); return; }
            Volatile.Write(ref _providerActive, 1);
            Report("Razer provider initialized • " + (multiband ? "EXPERIMENTAL multiband .haps, phase/transient loss • " :
                hybrid ? "EXPERIMENTAL hybrid (standard hits + multiband steady mixes) • " : "v2 stream ") + TickMs + " ms");

            var leftOut = new SideOutput(left);
            var rightOut = new SideOutput(right);
            long cursor = Features.CompletedSlots;
            SideFeatures lastL = default, lastR = default;
            double lastSourceAt = -1;
            double tickSec = TickMs / 1000.0;
            double next = clock.Elapsed.TotalSeconds;
            _lastRateStamp = Stopwatch.GetTimestamp(); _lastRateTicks = 0;
            int missingProviderTicks = 0;
            double silentSince = -1;
            bool idle = false, wasIdle = false;
            double idleRenderAt = 0;

            while (Volatile.Read(ref _stop) == 0)
            {
                double now = clock.Elapsed.TotalSeconds;
                SideFeatures fl, fr;
                double gain = SourceGainPercent / 100.0;
                bool manual = Environment.TickCount64 - Interlocked.Read(ref _lastSignalTick) <= 140;
                if (manual)
                {
                    // Test pulses: constant amplitude at a frequency close to resonance.
                    fl = Constant(BitConverter.Int32BitsToSingle(Volatile.Read(ref _leftAmplitudeBits)));
                    fr = Constant(BitConverter.Int32BitsToSingle(Volatile.Read(ref _rightAmplitudeBits)));
                    gain = 1.0;
                    cursor = Features.CompletedSlots;   // never replay stale source data afterwards
                }
                else
                {
                    int n = Features.Take(ref cursor, out fl, out fr);
                    if (n > 0) { lastL = fl; lastR = fr; lastSourceAt = now; }
                    else if (lastSourceAt >= 0 && now - lastSourceAt <= SourceTimeoutSeconds)
                    {
                        fl = Hold(lastL); fr = Hold(lastR);   // next capture block not arrived yet
                    }
                    else { fl = default; fr = default; }
                }
                double level = HapticOutputLevel.Apply(gain, OutputLevelPercent);
                // Idle gate (2026-11-07): a playing all-zero curve makes the controller hum, so after
                // 100 ms of silence the events are really stopped; the next sound restarts them.
                if (IdleGate && IsSilent(fl) && IsSilent(fr))
                {
                    if (silentSince < 0) silentSince = now;
                    if (!idle && now - silentSince >= IdleAfterSeconds)
                    {
                        Engine.StopAllEvents();
                        leftOut.Reset(); rightOut.Reset();
                        leftPolicy.Reset(); rightPolicy.Reset();
                        idle = true;
                    }
                }
                else { silentSince = -1; idle = false; }
                // While idle the controller queue is empty: poll input every 5 ms and send the
                // first frame of a new sound at once instead of waiting up to a whole frame; empty
                // frames keep the regular frame period.
                if (wasIdle && !idle) next = now;
                bool render = !idle || now >= idleRenderAt;
                if (!render) { wasIdle = idle; next = Math.Max(next, now) + IdlePollSeconds; WaitUntil(timer, clock, next); continue; }
                if (idle) idleRenderAt = now + tickSec;
                wasIdle = idle;
                if (!idle)
                {
                    if (multiband)
                    {
                        HapticSpectrum ls = default, rs = default;
                        if (manual) {
                            ls = new(new((float)TestFrequencyHz, fl.Peak, 0), default, default, 1);
                            rs = new(new((float)TestFrequencyHz, fr.Peak, 0), default, default, 1);
                        }
                        else if (!IsSilent(fl) || !IsSilent(fr)) Features.TryTakeSpectrum(out ls, out rs);
                        string lh = MultibandHaps.Build(ls, fl, level, out bool reducedL);
                        string rh = MultibandHaps.Build(rs, fr, level, out bool reducedR);
                        nativeStage = "multiband UpdateHM";
                        if (!Engine.UpdateHM(left, lh) || !Engine.UpdateHM(right, rh))
                            throw new InvalidOperationException("Experimental .haps update rejected; no single-band fallback.");
                        nativeStage = "multiband PlayEvent";
                        Engine.PlayEvent(left, -now, 0, 0); Engine.PlayEvent(right, -now, 0, 0);
                        MultibandFrames++;
                        if (reducedL || reducedR) MultibandHeadroomReductions++;
                    }
                    else if (hybrid)
                    {
                        HapticSpectrum ls = default, rs = default;
                        bool haveSpectrum = !manual && (!IsSilent(fl) || !IsSilent(fr)) && Features.TryTakeSpectrum(out ls, out rs);
                        nativeStage = "hybrid update";
                        HybridSide(leftPolicy, leftOut, leftHm, fl, ls, haveSpectrum, level, tickSec, now);
                        HybridSide(rightPolicy, rightOut, rightHm, fr, rs, haveSpectrum, level, tickSec, now);
                    }
                    else { leftOut.Apply(fl, level, tickSec, now); rightOut.Apply(fr, level, tickSec, now); }
                }
                if (!idle) LastSentHz = multiband ? (0, 0) : (leftOut.UsedHz, rightOut.UsedHz);
                if (!idle && hybrid) LastHybridMulti = (leftPolicy.Multi, rightPolicy.Multi);
                FrameProbe?.Invoke(fl, fr, level, idle);
                nativeStage = "ComputeAllEvents";
                Engine.ComputeAllEvents(now);
                if (Provider.ProviderIsPresent())
                {
                    missingProviderTicks = 0;
                    nativeStage = "provider render";
                    // The vendor debug accessor indexes a material frame; .haps idle after StopAllEvents
                    // has no material frame and throws natively. Normal render still publishes silence.
                    if (CaptureFrames && !idle)   // debug accessor throws (SEH) when idle-gated events have no material frame
                    {
                        int len = Provider.RenderHapticsDebug(_frameScratch, 0, _frameScratch.Length);
                        if (len > 0 && len <= _frameScratch.Length)
                        {
                            lock (_lock) _lastFrame = _frameScratch.AsSpan(0, len).ToArray();
                            CommandProbe?.Invoke(idle ? default : leftOut.LastCommand, idle ? default : rightOut.LastCommand, _frameScratch.AsSpan(0, len).ToArray());
                            if (len > 41) Interlocked.Increment(ref _transientFrames);
                        }
                    }
                    else {
                        Provider.ProviderRenderHaptics();
                        if (idle && CaptureFrames) lock (_lock) _lastFrame = Array.Empty<byte>();
                    }
                    Interlocked.Increment(ref _renderTicks);
                }
                else if (++missingProviderTicks >= 2000 / TickMs)
                {
                    Report("Razer provider absent for 2 seconds; native output stopped safely.");
                    break;
                }
                UpdateRate();

                // Absolute schedule: no drift, no 15.6 ms timer quantisation.
                next += idle ? IdlePollSeconds : tickSec;
                double wait = next - clock.Elapsed.TotalSeconds;
                if (wait < -0.050) { next = clock.Elapsed.TotalSeconds; continue; }   // fell behind: resync
                if (wait > 0) WaitSeconds(timer, wait);
            }

            // Leave the controller at an explicit zero amplitude before disconnecting.
            if (!IdleGate && providerInit && Provider.ProviderIsPresent())
            {
                for (int i = 0; i < ZeroOutTicks; i++)
                {
                    double t = clock.Elapsed.TotalSeconds;
                    if (multiband || hybrid) Engine.StopAllEvents();
                    else { leftOut.Apply(default, 1, tickSec, t); rightOut.Apply(default, 1, tickSec, t); }
                    Engine.ComputeAllEvents(t);
                    Provider.ProviderRenderHaptics();
                    WaitSeconds(timer, tickSec);
                }
            }
        }
        catch (Exception ex)
        {
            Report("Razer Sensa backend error at " + nativeStage + ": " + ex.GetType().Name + " - " + ex.Message + " (multiband updates " + MultibandFrames + ", slots " + Features.CompletedSlots + ")");
            DiagnosticsService.Error("Sensa engine", ex);
        }
        finally
        {
            Volatile.Write(ref _providerActive, 0);
            Volatile.Write(ref _leftAmplitudeBits, 0);
            Volatile.Write(ref _rightAmplitudeBits, 0);
            if (init)
            {
                try { Engine.StopAllEvents(); } catch { }
                if (IdleGate && Volatile.Read(ref _stop) != 0 && providerInit)
                {
                    // Leave Synapse with an empty frame, never the last vibrating one.
                    try
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            Engine.ComputeAllEvents(clock.Elapsed.TotalSeconds);
                            Provider.ProviderRenderHaptics();
                            Thread.Sleep(TickMs);
                        }
                    }
                    catch { }
                }
                if (providerInit) { try { Provider.ProviderClean(); } catch { } }
                try { Engine.Quit(); } catch { }
            }
            if (timer != IntPtr.Zero) Timing.CloseHandle(timer);
            if (periodSet) Timing.timeEndPeriod(1);
            if (mmcss != IntPtr.Zero) { try { Timing.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
        }
    }

    private static SideFeatures Constant(float amplitude) => new()
    {
        P0 = amplitude, P1 = amplitude, P2 = amplitude, P3 = amplitude,
        FrequencyHz = amplitude > 0 ? (float)TestFrequencyHz : 0
    };

    private const double IdleAfterSeconds = 0.100;
    private const int ZeroOutTicks = 10;
    private const double IdlePollSeconds = 0.005;

    /// <summary>Hybrid: which hands were in multiband on the last frame (diagnostics).</summary>
    public (bool Left, bool Right) LastHybridMulti { get; private set; }

    /// <summary>
    /// One hand, one hybrid frame. Multiband: the .haps note set gets the fast envelope split by the
    /// spectral shares and the parametric effect is sent silence. Standard: the parametric effect
    /// plays as usual and the .haps note is left to expire (its curve reaches zero at 66 ms).
    /// </summary>
    private void HybridSide(HybridOutputPolicy policy, SideOutput standard, int hm, in SideFeatures f,
        in HapticSpectrum spectrum, bool haveSpectrum, double level, double tickSec, double now)
    {
        bool wasMulti = policy.Multi;
        if (policy.Decide(f, spectrum, haveSpectrum, now))
        {
            // The parametric effect is STOPPED, not fed a zero curve: a playing all-zero curve hums at
            // its pitch (2026-11-07), which in the 90-case run put a mixed 80+190 Hz scene at ~130 Hz
            // although 90 % of its frames were multiband.
            if (!wasMulti) { Engine.StopEvent(standard.MaterialId); standard.Reset(); }
            var scaled = HybridOutputPolicy.ScaleToEnvelope(spectrum, f.Peak);
            string haps = MultibandHaps.Build(scaled, f, level, out bool reduced);
            if (!Engine.UpdateHM(hm, haps)) throw new InvalidOperationException("Hybrid .haps update rejected.");
            Engine.PlayEvent(hm, -now, 0, 0);
            HybridMultiFrames++;
            if (reduced) MultibandHeadroomReductions++;
        }
        else
        {
            if (wasMulti) Engine.StopEvent(hm);
            standard.Apply(HybridOutputPolicy.StandardFrame(f, spectrum, haveSpectrum), level, tickSec, now);
            if (f.Peak > HybridOutputPolicy.ActiveThreshold) HybridStandardFrames++;
        }
    }

    private static void WaitUntil(IntPtr timer, Stopwatch clock, double at)
    {
        double wait = at - clock.Elapsed.TotalSeconds;
        if (wait > 0) WaitSeconds(timer, wait);
    }

    /// <summary>
    /// true (default): after 100 ms of silence the events are stopped, so empty frames are sent.
    /// false: keep streaming a zero-amplitude curve. Measured 2026-11-07 at 33 ms frames: a playing
    /// zero curve makes the controller hum while idle (mic 1000-2400 vs 6-28 with the gate); with
    /// the gate, pulses and audio both end within ~0.2 s.
    /// </summary>
    public static volatile bool IdleGate = true;

    private static bool IsSilent(in SideFeatures f) =>
        f.TransientCount == 0 && !(f.P0 > 1e-4 || f.P1 > 1e-4 || f.P2 > 1e-4 || f.P3 > 1e-4);

    private static SideFeatures Hold(SideFeatures last) => new()
    {
        P0 = last.P3, P1 = last.P3, P2 = last.P3, P3 = last.P3, FrequencyHz = last.FrequencyHz
    };

    /// <summary>Maps a frequency in Hz to the engine's 0..1 pitch over 30–400 Hz.</summary>
    public static double PitchFor(double hz)
    {
        if (!(hz > 0)) hz = ResonanceHz;
        return Math.Clamp((hz - FrequencyMinHz) / (FrequencyMaxHz - FrequencyMinHz), 0, 1);
    }

    /// <summary>
    /// v2.1 output method. false (default): one long-running effect per side whose curve is
    /// rewritten every tick at the effect's own clock — no restart. true: v2.0 behaviour
    /// (short curve + PlayEvent restart every tick); kept only for A/B measurements.
    /// Frame dumps 2026-11-07 showed v2.0 setting the transient flag on every other frame
    /// although no transients were requested — every restart looked like a new onset.
    /// </summary>
    public static volatile bool RestartEachTick;

    /// <summary>Diagnostics switch: false drops transient (onset) points from the stream.</summary>
    public static volatile bool SendTransients = true;
    /// <summary>Direct USB: the first hit of a frame becomes a real transient record (decoded 2026-11-09);
    /// false = the older behaviour, folded into the nearest strength point (A/B measurements).</summary>
    public static volatile bool DirectTransientRecords = true;

    /// <summary>Diagnostics: called on the engine thread for every rendered frame (left, right, gain, idle).</summary>
    public Action<SideFeatures, SideFeatures, double, bool>? FrameProbe { get; set; }

    /// <summary>Diagnostics (CaptureFrames only): values sent per hand and the frame the provider rendered.</summary>
    public Action<SensaCommand, SensaCommand, byte[]>? CommandProbe { get; set; }

    /// <summary>Single-band diagnostics; 0 also means not applicable for experimental multiband output.</summary>
    public (double Left, double Right) LastSentHz { get; private set; }

    /// <summary>Per-side reusable arrays; no allocation per tick.</summary>
    private sealed class SideOutput
    {
        private const double RestartAfterSeconds = 300;
        private const double PitchHoldSeconds = 1.0;     // keep effect-time small and precise
        private readonly int _id;
        private readonly double[] _amp = new double[10];
        private readonly double[] _pitch = new double[4];
        private readonly double[] _tr2 = new double[6];
        private readonly double[] _tr1 = new double[3];
        private double _playStart = double.NaN;
        private double _lastHz, _lastHzAt = double.NaN;

        public SideOutput(int id) { _id = id; }

        public int MaterialId => _id;

        /// <summary>Frequency sent with the last frame (0 = engine default, the resonance).</summary>
        public double UsedHz { get; private set; }

        /// <summary>Events were stopped; the next Apply starts the effect again.</summary>
        public void Reset() => _playStart = double.NaN;

        public void Apply(in SideFeatures f, double gain, double tickSec, double now)
        {
            double a0 = HapticLimiter.Apply(f.P0, gain), a1 = HapticLimiter.Apply(f.P1, gain), a2 = HapticLimiter.Apply(f.P2, gain), a3 = HapticLimiter.Apply(f.P3, gain);
            bool restart = RestartEachTick || double.IsNaN(_playStart) || now - _playStart > RestartAfterSeconds;
            if (restart) _playStart = now;
            // Curve times are on the effect's own clock (0 = last PlayEvent). The stream is kept
            // warm: updated every tick even when silent (skipping silent ticks cost ~10 ms at onsets).
            double e = now - _playStart;
            _amp[0] = e; _amp[1] = a0;
            _amp[2] = e + tickSec / 3; _amp[3] = a1;
            _amp[4] = e + 2 * tickSec / 3; _amp[5] = a2;
            _amp[6] = e + tickSec; _amp[7] = a3;
            _amp[8] = e + tickSec + HoldSeconds; _amp[9] = a3;
            // Frequency unknown for a moment (onset before the 40 ms window fills, or a fading tail):
            // keep this side's last pitch instead of falling back to the 50 Hz resonance, which
            // shakes the whole shell. Measured 2026-11-07: every side switch of the audio ping-pong
            // started at "0 Hz" -> a 50 Hz kick felt in both hands; the demo path had 82 Hz there.
            double hz = f.FrequencyHz;
            if (hz > 0) { _lastHz = hz; _lastHzAt = now; }
            else if (now - _lastHzAt <= PitchHoldSeconds) hz = _lastHz;
            UsedHz = hz;
            double p = PitchFor(hz);
            _pitch[0] = e; _pitch[1] = p; _pitch[2] = e + tickSec + HoldSeconds; _pitch[3] = p;
            double[]? tr = null; int trLen = 0;
            if (SendTransients && f.TransientCount >= 1)
            {
                tr = f.TransientCount >= 2 ? _tr2 : _tr1;
                tr[0] = e + f.T0Position * tickSec; tr[1] = HapticLimiter.Apply(f.T0Amplitude, gain); tr[2] = p;
                if (f.TransientCount >= 2) { tr[3] = e + f.T1Position * tickSec; tr[4] = HapticLimiter.Apply(f.T1Amplitude, gain); tr[5] = p; }
                trLen = tr.Length;
            }
            Engine.UpdateParametricEffect(_id, _amp, _amp.Length, _pitch, _pitch.Length,
                FrequencyMinHz, FrequencyMaxHz, tr, trLen, false);
            if (restart) Engine.PlayEvent(_id, -now, 0, 0);
            LastCommand = new SensaCommand(a0, a1, a2, a3, p, hz, trLen / 3,
                trLen >= 3 ? f.T0Position : 0, trLen >= 3 ? tr![1] : 0, trLen >= 6 ? f.T1Position : 0, trLen >= 6 ? tr![4] : 0);
        }

        /// <summary>Diagnostics: the values sent with the last Apply (frame-format study).</summary>
        public SensaCommand LastCommand { get; private set; }

    }

    private void UpdateRate()
    {
        long ticks = Interlocked.Increment(ref _tickCount);
        long stamp = Stopwatch.GetTimestamp();
        double elapsed = (stamp - _lastRateStamp) / (double)Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            float hz = (float)((ticks - _lastRateTicks) / elapsed);
            Volatile.Write(ref _measuredTickHzBits, BitConverter.SingleToInt32Bits(hz));
            _lastRateTicks = ticks; _lastRateStamp = stamp;
        }
    }

    private static void WaitSeconds(IntPtr timer, double seconds)
    {
        if (timer != IntPtr.Zero)
        {
            long due = -(long)(seconds * 1e7);               // relative, 100 ns units
            if (Timing.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Timing.WaitForSingleObject(timer, 1000);
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)(seconds * 1000)));
    }

    // Test pattern: three distinct beats are far easier to notice in the hand than one
    // long buzz. Each OFF gap exceeds the 140 ms signal watchdog, so every beat is a new
    // onset and re-triggers PlayEvent.
    public const int PatternBeats = 3, PatternOnMs = 300, PatternOffMs = 220;

    public Task<bool> TestSideAsync(HapticTestChannel channel, CancellationToken token) =>
        PlayPatternAsync(channel == HapticTestChannel.Left, channel == HapticTestChannel.Right, token);

    /// <summary>Pattern on both actuators together.</summary>
    public Task<bool> TestBothAsync(CancellationToken token) => PlayPatternAsync(true, true, token);

    private async Task<bool> PlayPatternAsync(bool left, bool right, CancellationToken token)
    {
        if (!IsActive) return false;
        try
        {
            for (int beat = 0; beat < PatternBeats; beat++)
            {
                for (int elapsed = 0; elapsed < PatternOnMs; elapsed += 40)
                {
                    token.ThrowIfCancellationRequested();
                    SubmitClamped(left ? TestPulseAmplitude : 0, right ? TestPulseAmplitude : 0, TestPulseAmplitude);
                    await Task.Delay(40, token);
                }
                Submit(0, 0);
                if (beat < PatternBeats - 1) await Task.Delay(PatternOffMs, token);
            }
        }
        finally { Submit(0, 0); }
        return IsActive;
    }

    public void Stop()
    {
        Thread? worker;
        lock (_lifecycle)
        {
            Volatile.Write(ref _stop, 1);
            Submit(0, 0);
            worker = _worker;
        }
        if (worker == null || worker == Thread.CurrentThread) return;
        // On timeout retain the live worker reference; Start must never spawn a second one.
        if (!worker.Join(1800))
        {
            Report("Native provider stop pending; new start blocked until old worker has exited");
            return;
        }
        lock (_lifecycle)
        {
            // A concurrent Start may already own a newer worker after this one exited.
            // Never clear its new spectral source from the older Stop call.
            if (ReferenceEquals(_worker, worker))
            {
                Features.EnableSpectralAnalysis(false);
                Features.ClearForSilence();
                _worker = null;
            }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>One hand's command for one frame: 4 amplitude points, pitch (0..1 over 30–400 Hz), Hz, transients.</summary>
public readonly record struct SensaCommand(double A0, double A1, double A2, double A3, double Pitch, double Hz,
    int Transients, double T0Position, double T0Amplitude, double T1Position, double T1Amplitude);

/// <summary>Path from the engine to the controller.</summary>
public enum SensaTransport
{
    /// <summary>Licensed HAR + Razer provider → Synapse → USB (needs Synapse running and the Sensa module loaded).</summary>
    Synapse = 0,
    /// <summary>Own encoder → HID feature report on the Wolverine (USB cable, PC mode). No Synapse, no HAR.</summary>
    DirectUsb = 1,
}
