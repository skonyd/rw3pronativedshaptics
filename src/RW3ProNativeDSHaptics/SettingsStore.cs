using System;
using System.IO;
using System.Text.Json;

namespace RW3ProNativeDSHaptics;

public enum HapticMode { Normal, Audio, Native }

public sealed class UserSettings
{
    public HapticMode Mode { get; set; } = HapticMode.Normal;
    public HapticRoute Route { get; set; } = HapticRoute.NormalXInput;
    public int PreferredXInputSlot { get; set; } = -1;
    // Changes RW3-sent motor commands, not any real DualSense PCM reference.
    public int OutputLevelPercent { get; set; } = HapticOutputLevel.DefaultPercent; // -1 auto, 0..3 explicit; do not infer device identity
    // Native v2: source envelope -> Sensa amplitude gain (140 normal, 200 strong, 300 max).
    public int NativeGainPercent { get; set; } = 140;
    // Opt-in: richer .haps output; still lossy, phase/short-transient acceptance unproven.
    /// <summary>Legacy (pre-2026-11-08 builds): true meant Multiband. Read once into <see cref="OutputMethod"/>.</summary>
    public bool ExperimentalMultiband { get; set; }
    /// <summary>Wolverine output method: 0 Standard, 1 Multiband (experimental), 2 Hybrid (experimental).</summary>
    public int OutputMethod { get; set; }
    /// <summary>Path to the controller: 0 Synapse (HAR + Razer runtime), 1 Direct USB (own encoder, no Synapse).</summary>
    /// <summary>0 = Synapse, 1 = Direct USB (default: the app needs no Razer software).</summary>
    public int Transport { get; set; } = 1;
    /// <summary>Sesten titreşim sensitivity in percent (Normal 100 / Yüksek 160 / Çok yüksek 250).</summary>
    public int AudioSensitivityPercent { get; set; } = 100;
    /// <summary>While the virtual DualSense runs, hide the physical Xbox pad from games (HidHide).</summary>
    public bool HidePadInGames { get; set; } = true;
    /// <summary>DualSense trigger effects in Xbox mode: 1 = impulse-trigger motors (+ haptics on the body
    /// motors), 0 = classic (everything on the body motors, as in PC mode).</summary>
    public int TriggerFeel { get; set; } = 1;
    public XboxNativeMix XboxNativeMix { get; set; } = new();
    public bool AudioOutputEnabled { get; set; } = true;
    public AudioHapticProfile AudioProfile { get; set; }
    /// <summary>Audio mode, Xbox mode only: bass → heavy motor, treble → light motor (instead of left/right).
    /// Ignored while the controller is in PC mode.</summary>
    public bool XboxBassTreble { get; set; }
    /// <summary>PC mode, virtual DualSense: trigger effects as clicks/texture in the palms (Sensa) instead of body rumble.</summary>
    public bool PalmTriggerFeel { get; set; }
    /// <summary>Audio mode: vibration level follows the content, not the volume knob (AudioLevelShaper).</summary>
    public bool AudioAutoLevel { get; set; }
    /// <summary>Audio mode: slowly reduce sustained drone so hits stand out (AudioLevelShaper).</summary>
    public bool AudioHumSuppression { get; set; }
    /// <summary>Direct USB: hits as real Sensa transient records (true) or folded into strength points (false).</summary>
    public bool DirectTransientRecords { get; set; } = true;
    /// <summary>Audio to haptics sync delay: -1 automatic (Bluetooth output 300 ms, wired 0), else ms.</summary>
    public int AudioSyncDelayMs { get; set; } = -1;
}

public sealed class SettingsStore
{
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
    public string FilePath => Path.Combine(
        IsPortable ? Path.Combine(AppContext.BaseDirectory, "data") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RW3ProNativeDSHaptics"),
        "settings.json");

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch { return new UserSettings(); }
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
