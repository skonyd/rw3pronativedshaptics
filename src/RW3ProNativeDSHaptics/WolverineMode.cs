using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

public enum WolverineControllerMode { NotFound, Pc, Xbox }

/// <summary>Razer control packet on feature report 4 (MI_01 COL04), the same framing as the Sensa frames:
/// [0] report id 4, [1] status, [2] transaction id, [6] data size, [7] class, [8] command, [9..] arguments,
/// [89] XOR of bytes 3..88 (packet bytes 2..87). Pure; tests compare with packets captured from Synapse.</summary>
public static class RazerCommand
{
    public static byte[] Build(byte transactionId, byte commandClass, byte command, ReadOnlySpan<byte> args)
    {
        if (args.Length > 80) throw new ArgumentException("too many arguments");
        var r = new byte[SensaHidTransport.FeatureLength];
        r[0] = SensaHidTransport.ReportId;
        r[2] = transactionId;
        r[6] = (byte)args.Length;
        r[7] = commandClass;
        r[8] = command;
        args.CopyTo(r.AsSpan(9));
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= r[i];
        r[89] = crc;
        return r;
    }

    /// <summary>What Synapse sends for its "Xbox mode" button (USB capture 2026-11-08, PC → Xbox):
    /// a per-mode setting (class 0x0C, cmd 0x95, args 01 02 00, answered OK), then class 0x00 cmd 0x53
    /// arg 01, repeated every 80 ms (status stays "busy") until the controller drops off the bus and
    /// returns as the Xbox-mode device 1532:0A3F about 1 s later.</summary>
    public static byte[] XboxModePrepare(byte tx = 0x00) => Build(tx, 0x0C, 0x95, [0x01, 0x02, 0x00]);
    public static byte[] XboxModeSwitch(byte tx = 0x01) => Build(tx, 0x00, 0x53, [0x01]);
}

/// <summary>
/// Wolverine V3 Pro mode: PC (1532:0A4C, XUSB + Razer HID: Sensa palm haptics, no trigger motors) or
/// Xbox (1532:0A3F, GIP: impulse-trigger motors, no Sensa). PC → Xbox can be switched from software
/// (Synapse's command, replayed); Xbox → PC only with the controller's own key combination.
/// </summary>
public static class WolverineModeControl
{
    public const string PcComboHint = "PC moduna dönmek için kolda ○ + Menü + A tuşlarını 2 sn birlikte basılı tut.";

    public static WolverineControllerMode Detect()
    {
        var nodes = HidHideControl.PadGameNodes();
        if (nodes.Any(n => n.Contains("PID_0A4C", StringComparison.OrdinalIgnoreCase))) return WolverineControllerMode.Pc;
        if (nodes.Any(n => n.Contains("PID_0A3F", StringComparison.OrdinalIgnoreCase))) return WolverineControllerMode.Xbox;
        return WolverineControllerMode.NotFound;
    }

    /// <summary>Sends Synapse's PC → Xbox sequence and waits for the Xbox-mode device. Returns a short result.</summary>
    public static async Task<(bool Ok, string Message)> SwitchToXboxAsync(CancellationToken token = default)
    {
        if (Detect() != WolverineControllerMode.Pc) return (false, "Kol PC modunda değil (zaten Xbox modunda ya da bağlı değil).");
        using var tx = new SensaHidTransport();
        if (!tx.Open()) return (false, "Kolun kontrol arabirimi açılamadı.");
        if (!tx.SendRaw(RazerCommand.XboxModePrepare())) return (false, "Mod komutu gönderilemedi (hata " + tx.LastError + ").");
        await Task.Delay(80, token);
        byte id = 1;
        for (int i = 0; i < 40; i++)                       // ≈ 3.2 s; Synapse needed ~1 s
        {
            if (!tx.SendRaw(RazerCommand.XboxModeSwitch(id++))) break;   // fails once the controller has left the bus
            await Task.Delay(80, token);
        }
        tx.Close();
        for (int i = 0; i < 40; i++)
        {
            if (Detect() == WolverineControllerMode.Xbox) return (true, "Kol Xbox modunda: tetik motorları açık.");
            await Task.Delay(250, token);
        }
        return (false, "Komut gönderildi ama kol Xbox modunda görünmedi.");
    }
}
