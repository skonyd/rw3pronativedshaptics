using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RW3TestRange;

/// <summary>
/// One test session: every game event and every feedback the game sent, as JSON lines, plus a readable summary at the
/// end that also pulls RW3's own log lines from the same time window (what RW3 received and did with it).
/// Folder: %LOCALAPPDATA%\RW3TestRange\logs.
/// </summary>
public sealed class SessionLog : IDisposable
{
    public static SessionLog? Current { get; private set; }
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3TestRange", "logs");
    public static string Rw3Log => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "logs", "rw3.log");

    private readonly StreamWriter _w;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    public DateTime Started { get; } = DateTime.Now;
    public string JsonPath { get; }
    public readonly Dictionary<string, long> Counters = new();
    public readonly List<string> Notes = new();

    public SessionLog()
    {
        Directory.CreateDirectory(Folder);
        JsonPath = Path.Combine(Folder, $"session-{Started:yyyyMMdd-HHmmss}.jsonl");
        _w = new StreamWriter(JsonPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        Current = this;
    }

    public double Seconds => _clock.Elapsed.TotalSeconds;

    public void Count(string key, long by = 1) { lock (_gate) Counters[key] = Counters.GetValueOrDefault(key) + by; }

    public void Event(string type, object? data = null)
    {
        string json = JsonSerializer.Serialize(new { t = Math.Round(Seconds, 3), wall = DateTime.Now.ToString("HH:mm:ss.fff"), type, data });
        lock (_gate) { try { _w.WriteLine(json); } catch { } }
    }

    public void Note(string text) { lock (_gate) Notes.Add($"[{Seconds,7:0.0} s] {text}"); Event("note", text); }

    /// <summary>Writes the summary (counters, notes, RW3 log lines of this session) and returns its path.</summary>
    public string Finish(string extra)
    {
        var end = DateTime.Now;
        var sb = new StringBuilder();
        sb.AppendLine($"Şişe Atışı test oturumu  {Started:yyyy-MM-dd HH:mm:ss} → {end:HH:mm:ss}  ({(end - Started).TotalMinutes:0.0} dk)");
        sb.AppendLine("Ayrıntılı olaylar: " + JsonPath);
        sb.AppendLine();
        sb.AppendLine(extra);
        sb.AppendLine("── Sayaçlar ──");
        lock (_gate) foreach (var kv in Counters.OrderBy(k => k.Key)) sb.AppendLine($"  {kv.Key,-40} {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("── Notlar ──");
        lock (_gate) foreach (var n in Notes) sb.AppendLine("  " + n);
        sb.AppendLine();
        sb.AppendLine("── RW3 kaydı (bu oturum süresince) ──");
        try
        {
            if (File.Exists(Rw3Log))
            {
                using var fs = new FileStream(Rw3Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                string? line; int n = 0;
                while ((line = r.ReadLine()) != null)
                    if (line.Length > 19 && DateTime.TryParse(line[..19], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) &&
                        t >= Started.AddSeconds(-5) && t <= end.AddSeconds(2)) { sb.AppendLine("  " + line); n++; }
                if (n == 0) sb.AppendLine("  (bu sürede RW3 kayıt satırı yok: RW3 kapalı mıydı?)");
            }
            else sb.AppendLine("  RW3 kaydı bulunamadı: " + Rw3Log);
        }
        catch (Exception ex) { sb.AppendLine("  okunamadı: " + ex.Message); }
        string path = Path.ChangeExtension(JsonPath, ".txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Event("session-end", new { summary = path });
        return path;
    }

    public void Dispose() { lock (_gate) { try { _w.Dispose(); } catch { } } if (Current == this) Current = null; }
}
