#if HAS_HIDMAESTRO
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HIDMaestro;

namespace RW3ProNativeDSHaptics;

/// <summary>Experiment (E-20261123-84): the virtual DualSense as a full-speed USB device instead of high speed.
/// HIDMaestro's DualSense presents the real pad's high-speed descriptors; Windows then drains its audio endpoint in
/// 20 ms bursts (a real DualSense: every 10 ms), and TLOU2's TV sound loses 64 samples every 140 ms (E-20261123-81..83).
/// The real pad's descriptors carry its full-speed configuration too (other-speed configuration: the same interfaces,
/// isochronous endpoints with interval 1). Armed only by <c>%LOCALAPPDATA%\RW3ProNativeDSHaptics\ds-fullspeed.flag</c>;
/// without it RW3 uses HIDMaestro's profile unchanged. Delete the flag and restart RW3 to go back.</summary>
public static class DualSenseAudioProfileExperiment
{
    public const string ProfileId = "rw3-dualsense-fullspeed";
    public static string FlagFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics", "ds-fullspeed.flag");

    public static HMProfile? TryLoad(HMContext ctx)
    {
        if (!File.Exists(FlagFile)) return null;
        try
        {
            var asm = typeof(HMContext).Assembly;
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("dualsense-composite.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) { DiagnosticsService.Info("DualSense full-speed experiment: HIDMaestro's profile resource not found; standard profile used."); return null; }
            string json; using (var s = asm.GetManifestResourceStream(name)!) using (var r = new StreamReader(s)) json = r.ReadToEnd();
            string dir = Path.Combine(Path.GetTempPath(), "rw3-dualsense-fullspeed");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProfileId + ".json"), ToFullSpeed(json));
            ctx.LoadProfilesFromDirectory(dir);
            var issues = string.Join(" | ", ctx.ProfileLoadIssues.Where(i => i.ProfileId == ProfileId || (i.File ?? "").Contains(ProfileId)).Select(i => $"{i.JsonPath}: {i.Reason}"));
            var profile = ctx.GetProfile(ProfileId);
            DiagnosticsService.Info("DualSense full-speed experiment: " + (profile != null ? "profile loaded" : "profile NOT loaded, standard profile used") + (issues.Length > 0 ? " (" + issues + ")" : "") + ".");
            return profile;
        }
        catch (Exception ex) { DiagnosticsService.Info("DualSense full-speed experiment failed, standard profile used: " + ex.Message); return null; }
    }

    /// <summary>The same profile as a full-speed device: the other-speed configuration becomes the configuration (and
    /// the high-speed one the other-speed one), isochronous endpoint intervals 4 → 1 to match it. Pure.</summary>
    public static string ToFullSpeed(string profileJson)
    {
        var root = JsonNode.Parse(profileJson)!.AsObject();
        root["id"] = ProfileId;
        root["name"] = "DualSense (PS5): Full, full-speed USB (RW3 experiment)";
        var usb = root["usbConfiguration"]!.AsObject();
        usb["busSpeed"] = "full";
        string hs = usb["configurationDescriptor"]!.GetValue<string>(), fs = usb["otherSpeedConfigurationDescriptor"]!.GetValue<string>();
        usb["configurationDescriptor"] = fs;
        usb["otherSpeedConfigurationDescriptor"] = hs;
        foreach (var itf in usb["interfaces"]!.AsArray())
            foreach (var alt in itf!["altSettings"]!.AsArray())
                if (alt!["endpoints"] is JsonArray eps)
                    foreach (var ep in eps)
                        if ((string?)ep!["transferType"] == "isochronous") ep["interval"] = 1;
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
#endif
