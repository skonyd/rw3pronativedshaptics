using System;
using System.Diagnostics;
using System.IO;

namespace RW3ProNativeDSHaptics;

/// <summary>Minimal transition/error journal. Never write raw audio or controller inputs.</summary>
public static class DiagnosticsService
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RW3ProNativeDSHaptics", "logs");
    public static string LogFile => Path.Combine(DirectoryPath, "rw3.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string context, Exception exception) =>
        Write("ERROR", context + " | " + exception.GetType().Name + ": " + exception.Message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = LogFile;
                if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
                {
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging failure must not crash or block gameplay. */ }
    }
}

public sealed class PerformanceMonitor : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTicks;
    public double CpuPercent { get; private set; }
    public double MemoryMb { get; private set; }
    public int SustainedHighSamples { get; private set; }
    public const double CpuBudgetPercent = 3.0; // Whole-system normalized CPU share

    public PerformanceMonitor()
    {
        _lastCpu = _process.TotalProcessorTime;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    public void Sample()
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        long ticks = Stopwatch.GetTimestamp();
        double elapsed = (ticks - _lastTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 0.3) return;
        double used = (cpu - _lastCpu).TotalSeconds;
        CpuPercent = Math.Clamp(used / (elapsed * Math.Max(1, Environment.ProcessorCount)) * 100, 0, 100);
        MemoryMb = _process.WorkingSet64 / 1048576.0;
        SustainedHighSamples = CpuPercent > CpuBudgetPercent ? SustainedHighSamples + 1 : 0;
        _lastCpu = cpu;
        _lastTicks = ticks;
    }

    public void Dispose() => _process.Dispose();
}
