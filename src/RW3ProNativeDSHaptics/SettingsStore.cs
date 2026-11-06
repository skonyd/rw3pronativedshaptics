using System;
using System.IO;
using System.Text.Json;

namespace RW3ProNativeDSHaptics;

public enum HapticMode { Normal, Audio, Native }

public sealed class UserSettings
{
    public HapticMode Mode { get; set; } = HapticMode.Normal;
    public HapticRoute Route { get; set; } = HapticRoute.NormalXInput;
    public int PreferredXInputSlot { get; set; } = -1; // -1 auto, 0..3 explicit; do not infer device identity
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
