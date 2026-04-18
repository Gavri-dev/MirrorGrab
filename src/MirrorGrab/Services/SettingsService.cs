using System.Text.Json;

namespace MirrorGrab.Services;

public class AppSettings
{
    public string DownloadDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int ConcurrentDownloads { get; set; } = 8;
    public bool AutoExtract { get; set; }
    public bool CloseToTray { get; set; }
}

public static class SettingsService
{
    private static readonly string SettingsFile = Path.Combine(AppDataDir, "settings.json");

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MirrorGrab");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
