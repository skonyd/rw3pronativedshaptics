using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
#if HAS_HIDMAESTRO
using HIDMaestro;
#endif

namespace RW3ProNativeDSHaptics;

/// <summary>
/// One-shot experiment for docs/research/GIP_VIRTUAL_PAD.md, question 1: does Windows' Xbox driver bind to a virtual USB
/// device of class FF/47/D0 (a wired Xbox pad), and what does it send it? Runs only when the flag file exists while RW3
/// starts elevated; the flag is deleted first, so it runs once. It creates one virtual device (HIDMaestro usbip, a custom
/// profile written to %TEMP%), sends a GIP announce every 500 ms, logs every host write and the device's driver for
/// 25 s, then removes the device. Nothing is sent to the real pad; nothing is installed.
/// </summary>
public static class GipVirtualPadProbe
{
    public const string ProfileId = "rw3-gip-probe";
    public static string FlagFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "gip-probe.flag");

    /// <summary>Wired Xbox Series controller identity (045E:0B12). Interface 0 is the GIP interface, class FF/47/D0 with
    /// interrupt OUT 0x02 / IN 0x82 (64 bytes, 4 ms), like a Series pad's. HIDMaestro refuses a configuration without a
    /// HID interface (first run, 2026-11-23: "configurationDescriptor: has no HID interface"), so interface 1 is a
    /// placeholder HID (vendor page, IN 0x81) and the device is composite (class 00): Windows then binds a driver per
    /// interface, and the question is whether its Xbox driver takes interface 0.</summary>
    public static string BuildProfileJson() => """
        {
          "id": "rw3-gip-probe",
          "name": "RW3 GIP probe (wired Xbox pad)",
          "vendor": "RW3",
          "vid": "0x045E",
          "pid": "0x0B12",
          "productString": "Controller",
          "manufacturerString": "Microsoft",
          "serialString": "RW3GIPPROBE0001",
          "type": "gamepad",
          "connection": "usb",
          "backend": "usbip",
          "descriptor": "0600ff0901a101150026ff007508954009018102954009019102c0",
          "inputReportSize": 64,
          "usbConfiguration": {
            "configurationValue": 1,
            "attributes": 160,
            "maxPowerMilliamps": 500,
            "busSpeed": "full",
            "deviceDescriptor": "12010002000000405e04120b080501020301",
            "configurationDescriptor": "09023900020100a0fa0904000002ff47d0000705020340000407058203400004090401000103000000092111010001221b0007058103400004",
            "interfaces": [
              { "interfaceNumber": 0, "function": "vendor",
                "altSettings": [ { "altSetting": 0, "interfaceClass": 255, "interfaceSubClass": 71, "interfaceProtocol": 208,
                  "endpoints": [ { "address": 2, "transferType": "interrupt", "maxPacketSize": 64, "interval": 4 },
                                 { "address": 130, "transferType": "interrupt", "maxPacketSize": 64, "interval": 4 } ] } ] },
              { "interfaceNumber": 1, "function": "hid",
                "altSettings": [ { "altSetting": 0, "interfaceClass": 3, "interfaceSubClass": 0, "interfaceProtocol": 0,
                  "endpoints": [ { "address": 129, "transferType": "interrupt", "maxPacketSize": 64, "interval": 4 } ] } ] }
            ]
          },
          "protocol": {
            "input": { "endpoint": "0x82" },
            "commands": { "endpoint": "0x02", "rules": [] }
          }
        }
        """;

    /// <summary>A GIP announce as the Wolverine sends it (E-20261122-61), with 045E:0B12 and a locally administered
    /// address: [address 6][2][vid][pid][firmware 4×u16][hardware 4×u16].</summary>
    public static byte[] Announce(byte sequence) => GipCodec.Encode(GipCodec.CmdAnnounce, GipCodec.OptInternal, sequence, new byte[]
    {
        0x02, 0x52, 0x57, 0x33, 0x00, 0x01, 0x00, 0x00, 0x5E, 0x04, 0x12, 0x0B,
        0x05, 0x00, 0x17, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00
    });

    public static void RunIfRequested()
    {
        try
        {
            if (!Environment.IsPrivilegedProcess || !File.Exists(FlagFile)) return;
            File.Delete(FlagFile);   // once
        }
        catch { return; }
        new Thread(Run) { IsBackground = true, Name = "RW3 GIP probe" }.Start();
    }

    private static void Log(string m) => DiagnosticsService.Info("GIP probe: " + m);
#if HAS_HIDMAESTRO
    private static HMController? ctrlRef;
#endif

    private static void Run()
    {
        Thread.Sleep(10_000);   // let RW3's own start (pad, virtual pad) settle first
#if HAS_HIDMAESTRO
        string dir = Path.Combine(Path.GetTempPath(), "rw3-gip-probe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProfileId + ".json"), BuildProfileJson(), new UTF8Encoding(false));
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            int loaded = ctx.LoadProfilesFromDirectory(dir);
            Log($"profiles loaded from {dir}: {loaded}; issues: " +
                string.Join(" | ", ctx.ProfileLoadIssues.Where(i => i.ProfileId == ProfileId || (i.File ?? "").Contains("rw3-gip")).Select(i => $"{i.Kind} {i.JsonPath}: {i.Reason}")));
            var profile = ctx.GetProfile(ProfileId);
            if (profile == null) { Log("profile not available, stopping."); return; }
            // question 2: answer Windows' identify request with the Wolverine's own identify data (read by direct mode
            // in this run, or saved by an earlier one)
            byte[]? ident = null;
            for (int i = 0; i < 30 && ident == null; i++)
            {
                ident = WinUsbGipTransport.Shared.IdentifyData;
                if (ident == null && File.Exists(WinUsbGipTransport.IdentifyFile)) ident = File.ReadAllBytes(WinUsbGipTransport.IdentifyFile);
                if (ident == null) Thread.Sleep(500);
            }
            Log(ident == null ? "no identify data from the pad: identify requests stay unanswered." : $"identify data {ident.Length} bytes ready.");
            int outputs = 0, identReplies = 0;
            byte devSeq = 1;
            var sendGate = new object();
            void SendRaw(byte[] msg)
            {
                var report = new byte[64];
                msg.AsSpan(0, Math.Min(64, msg.Length)).CopyTo(report);
                lock (sendGate) try { ctrlRef!.SubmitRawReport(report); } catch (Exception ex) { Log("SubmitRawReport failed: " + ex.Message); }
            }
            using var ctrl = ctx.CreateController(profile, "rw3-gip-probe");
            ctrlRef = ctrl;
            Log("virtual device created.");
            ctrl.OutputReceived += (_, pkt) =>
            {
                int n = Interlocked.Increment(ref outputs);
                // Data = the message after its command byte: [options][sequence][length…]; an identify request has length 0
                // (run 4 checked the sequence byte instead and never answered)
                bool identifyRequest = pkt.ReportId == GipCodec.CmdIdentify && pkt.Data.Length >= 3 && pkt.Data.Span[2] == 0;
                // the identify requests repeat every 500 ms until answered: log the first few, and everything else
                if (n <= 12 || !identifyRequest && n <= 200) Log($"host write #{n}: source {pkt.Source} report 0x{pkt.ReportId:X2} len {pkt.Data.Length}: {Convert.ToHexString(pkt.Data.Span[..Math.Min(48, pkt.Data.Length)])}");
                if (identifyRequest && ident != null && Interlocked.Increment(ref identReplies) <= 6)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var chunks = GipCodec.EncodeChunks(GipCodec.CmdIdentify, devSeq++, ident);
                        foreach (var c in chunks) { SendRaw(c); Thread.Sleep(8); }
                        Log($"identify reply sent in {chunks.Count} chunks.");
                    });
            };
            ctrl.OutputDecoded += (_, a) => Log($"decoded report 0x{a.ReportId:X2}: {Convert.ToHexString(a.RawBytes.Span[..Math.Min(48, a.RawBytes.Length)])}");
            var sw = Stopwatch.StartNew();
            byte seq = 1;
            bool driverLogged = false;
            while (sw.Elapsed.TotalSeconds < 30)
            {
                if (Volatile.Read(ref outputs) == 0 || sw.Elapsed.TotalSeconds < 3)
                {
                    SendRaw(Announce(seq++));
                }
                if (!driverLogged && sw.Elapsed.TotalSeconds > 6) { driverLogged = true; Log("PnP: " + DeviceState()); }
                Thread.Sleep(500);
            }
            Log($"done: {outputs} host writes, {identReplies} identify requests answered in 30 s; removing the device.");
        }
        catch (Exception ex) { Log("failed: " + ex.GetType().Name + ": " + ex.Message); }
#else
        Log("built without HIDMaestro.");
#endif
    }

    /// <summary>pnputil's view of 045E:0B12 devices (driver and status), first lines only.</summary>
    private static string DeviceState()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/enum-devices /connected /drivers")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            var blocks = all.Split(new[] { "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(b => b.Contains("VID_045E&PID_0B12", StringComparison.OrdinalIgnoreCase)).ToList();
            return blocks.Count == 0 ? "no 045E:0B12 device" : string.Join(" || ", blocks.Select(b => string.Join(" ; ", b.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(12))));
        }
        catch (Exception ex) { return "pnputil failed: " + ex.Message; }
    }
}
