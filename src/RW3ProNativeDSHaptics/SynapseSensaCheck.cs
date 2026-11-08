using System;
using System.IO;
using System.Linq;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Read-only check of whether Synapse has attached a Wolverine to its haptics service, from Razer's own
/// log (RzInterHaptics.log): each sender that reaches a device logs `SenderGetMetadata device "2636-{…}"`.
/// 2026-11-08: after a Windows restart (and on the dongle alone) senders registered without that line
/// and nothing reached the motors until the Sensa page was opened / the cable was used.
/// </summary>
public static class SynapseSensaCheck
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "RazerAppEngine", "User Data", "Logs", "RzInterHaptics.log");

    /// <summary>True when the latest sender registration in the log is followed by a device line.</summary>
    public static bool LooksAttached(out string detail)
    {
        try
        {
            if (!File.Exists(LogPath)) { detail = "Synapse haptik günlüğü yok"; return false; }
            string[] lines;
            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                lines = sr.ReadToEnd().Split('\n').TakeLast(400).ToArray();
            return Evaluate(lines, out detail);
        }
        catch (Exception ex) { detail = "günlük okunamadı: " + ex.Message; return false; }
    }

    /// <summary>Pure evaluation of log lines (tests).</summary>
    public static bool Evaluate(string[] lines, out string detail)
    {
        int lastSender = Array.FindLastIndex(lines, l => l.Contains("New sender registered"));
        int lastDevice = Array.FindLastIndex(lines, l => l.Contains("SenderGetMetadata device \"2636-"));
        int lastStart = Array.FindLastIndex(lines, l => l.Contains("Starting the RPC server"));
        if (lastDevice >= 0 && lastDevice >= lastSender && lastDevice > lastStart) { detail = "Wolverine bağlı"; return true; }
        if (lastSender < 0) { detail = "henüz bağlanan uygulama yok"; return lastDevice > lastStart; }
        detail = "son bağlantıda Wolverine görünmedi";
        return false;
    }
}
