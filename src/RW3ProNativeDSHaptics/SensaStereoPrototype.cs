using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// EXPERIMENTAL stereo envelope output to the public Interhaptics engine + Razer provider.
/// A parametric amplitude envelope is NOT the original 48 kHz DualSense PCM waveform.
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
        [DllImport(Dll)] public static extern void AddTargetToEventMarshal(
            int effectId, [In] CommandData[] targets, int count);
        [DllImport(Dll)] public static extern void SetEventIntensity(int id, double intensity);
        [DllImport(Dll)] public static extern void PlayEvent(int id, double vibrationOffset,
            double textureOffset, double stiffnessOffset);
        [DllImport(Dll)] public static extern void ComputeAllEvents(double currentSeconds);
        [DllImport(Dll)] public static extern void StopAllEvents();
    }

    private static class Provider
    {
        private const string Dll = "Interhaptics.RazerProvider";
        [DllImport(Dll)] public static extern bool ProviderInit();
        [DllImport(Dll)] public static extern bool ProviderIsPresent();
        [DllImport(Dll)] public static extern void ProviderRenderHaptics();
        [DllImport(Dll)] public static extern bool ProviderClean();
    }

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

    // Shared types (official): GroupID.Palm=400, LateralFlag.Left=2, Right=1.
    public const int PalmGroup = 400;
    public const int LeftSide = 2, RightSide = 1;
    public const int TickMs = 20; // <=50 engine ticks/s, not 48k raw audio replay.

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
            if (!File.Exists(har) || !File.Exists(provider))
            {
                Report("Official HAR.dll and Interhaptics.RazerProvider.dll not found beside app. " +
                    "Obtain them from the licensed WYVRN SDK, then restart.");
                return false;
            }
            Volatile.Write(ref _stop, 0);
            Volatile.Write(ref _providerActive, 0);
            Volatile.Write(ref _renderTicks, 0);
            _worker = new Thread(Run) { IsBackground = true, Name = "RW3-SensaStereo" };
            _worker.Start();
            return true;
        }
    }

    public void Submit(float left, float right)
    {
        // P/Invoke must never receive NaN/Infinity from user or source data.
        left = float.IsFinite(left) ? Math.Clamp(left, 0, 0.38f) : 0;
        right = float.IsFinite(right) ? Math.Clamp(right, 0, 0.38f) : 0;
        Volatile.Write(ref _leftAmplitudeBits, BitConverter.SingleToInt32Bits(left));
        Volatile.Write(ref _rightAmplitudeBits, BitConverter.SingleToInt32Bits(right));
        Interlocked.Exchange(ref _lastSignalTick, Environment.TickCount64);
    }

    private void Run()
    {
        bool init = false, providerInit = false;
        try
        {
            Report("Starting HAR.dll engine");
            init = Engine.Init();
            if (!init) { Report("HAR.Init returned false"); return; }
            var clock = Stopwatch.StartNew();
            // One monotone intensity-shape effect per side, independent of game audio playback.
            double[] envelope = { 0, 1, 1, 1 };
            int left = Engine.AddParametricEffect(envelope, 4, null, 0, 65, 300, null, 0, true);
            int right = Engine.AddParametricEffect(envelope, 4, null, 0, 65, 300, null, 0, true);
            if (left < 0 || right < 0) { Report("Failed to create stereo parametric effects"); return; }
            Engine.AddTargetToEventMarshal(left, new[] { new CommandData(PalmGroup, LeftSide) }, 1);
            Engine.AddTargetToEventMarshal(right, new[] { new CommandData(PalmGroup, RightSide) }, 1);
            Engine.SetEventIntensity(left, 0);
            Engine.SetEventIntensity(right, 0);
            Engine.PlayEvent(left, -clock.Elapsed.TotalSeconds, 0, 0);
            Engine.PlayEvent(right, -clock.Elapsed.TotalSeconds, 0, 0);

            Report("Interhaptics engine started; finding Razer runtime");
            providerInit = Provider.ProviderInit();
            if (!providerInit) { Report("Razer runtime unavailable; install Synapse 4 + Chroma + PC mode"); return; }
            Volatile.Write(ref _providerActive, 1);
            Report("Razer provider initialized (physical LEFT/RIGHT haptics NOT YET verified)");

            int missingProviderTicks = 0;
            while (Volatile.Read(ref _stop) == 0)
            {
                bool fresh = Environment.TickCount64 - Interlocked.Read(ref _lastSignalTick) <= 140;
                float la = fresh ? BitConverter.Int32BitsToSingle(Volatile.Read(ref _leftAmplitudeBits)) : 0;
                float ra = fresh ? BitConverter.Int32BitsToSingle(Volatile.Read(ref _rightAmplitudeBits)) : 0;
                Engine.SetEventIntensity(left, HapticOutputLevel.Apply(la, OutputLevelPercent));
                Engine.SetEventIntensity(right, HapticOutputLevel.Apply(ra, OutputLevelPercent));
                Engine.ComputeAllEvents(clock.Elapsed.TotalSeconds);
                if (Provider.ProviderIsPresent())
                {
                    missingProviderTicks = 0;
                    Provider.ProviderRenderHaptics();
                    Interlocked.Increment(ref _renderTicks);
                }
                else if (++missingProviderTicks >= 100)
                {
                    Report("Razer provider absent for 2 seconds; native output stopped safely.");
                    break;
                }
                Thread.Sleep(TickMs);
            }
        }
        catch (Exception ex)
        {
            Report("Razer Sensa experimental backend error: " + ex.GetType().Name + " - " + ex.Message);
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
                if (providerInit) { try { Provider.ProviderClean(); } catch { } }
                try { Engine.Quit(); } catch { }
            }
        }
    }

    public async Task<bool> TestSideAsync(HapticTestChannel channel, CancellationToken token)
    {
        if (!IsActive) return false;
        try
        {
            // Keep each test pulse fresh past the normal 140ms signal watchdog.
            for (int i = 0; i < 7; ++i)
            {
                token.ThrowIfCancellationRequested();
                Submit(channel == HapticTestChannel.Left ? 0.20f : 0,
                       channel == HapticTestChannel.Right ? 0.20f : 0);
                await Task.Delay(55, token);
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
            if (ReferenceEquals(_worker, worker)) _worker = null;
    }

    public void Dispose() => Stop();
}
