using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlcoRats;

namespace AlcoRats.Settings;

public class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    public string RoomPassword { get; set; } = "1992";

    public string UserEnter { get; set; } = "{User}, закотился";

    public string UserLeave { get; set; } = "{User}, выкатился";

    public bool ToolbarAlwaysVisible { get; set; } = true;

    [JsonIgnore]
    public static string SettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AlcoRats",
        "settings.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return;

            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
                return;

            this.CopyFrom(loaded);
        }
        catch
        {
            // Keep in-memory defaults if the file is missing or corrupt.
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }
}
