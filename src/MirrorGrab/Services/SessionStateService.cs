using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorGrab.Models;

namespace MirrorGrab.Services;

public static class SessionStateService
{
    private static readonly string StateFile =
        Path.Combine(SettingsService.AppDataDir, "session_state.json");

    public static void Save(IEnumerable<DownloadBatch> batches)
    {
        try
        {
            var data = batches.Select(b => new BatchStateData
            {
                Name = b.Name,
                AutoExtract = b.AutoExtract,
                Items = b.Items.Select(i => new ItemStateData
                {
                    OriginalUrl = i.OriginalUrl,
                    DirectUrl = i.DirectUrl,
                    Filename = i.Filename,
                    Filepath = i.Filepath,
                    DownloadDir = i.DownloadDir,
                    TotalSize = i.TotalSize,
                    DownloadedSize = i.DownloadedSize,
                    StatusStr = i.Status.ToString().ToLower(),
                    Error = i.Error,
                }).ToList()
            }).ToList();

            var json = JsonSerializer.Serialize(data, AppJsonContext.Default.ListBatchStateData);
            // Write-then-rename so a crash mid-write can't truncate the existing state file.
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch { }
    }

    public static List<BatchStateData> Load()
    {
        try
        {
            if (!File.Exists(StateFile)) return [];
            var json = File.ReadAllText(StateFile);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ListBatchStateData) ?? [];
        }
        catch { return []; }
    }
}

public class BatchStateData
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("auto_extract")]
    public bool AutoExtract { get; set; }

    [JsonPropertyName("items")]
    public List<ItemStateData> Items { get; set; } = [];
}

public class ItemStateData
{
    [JsonPropertyName("original_url")]
    public string OriginalUrl { get; set; } = "";

    [JsonPropertyName("direct_url")]
    public string? DirectUrl { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("filepath")]
    public string? Filepath { get; set; }

    [JsonPropertyName("download_dir")]
    public string? DownloadDir { get; set; }

    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }

    [JsonPropertyName("downloaded_size")]
    public long DownloadedSize { get; set; }

    [JsonPropertyName("status")]
    public string StatusStr { get; set; } = "queued";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public DownloadStatus ParseStatus()
    {
        return StatusStr.ToLower() switch
        {
            "queued" => DownloadStatus.Queued,
            "extracting" => DownloadStatus.Extracting,
            "downloading" => DownloadStatus.Downloading,
            "paused" => DownloadStatus.Paused,
            "completed" => DownloadStatus.Completed,
            "failed" => DownloadStatus.Failed,
            "skipped" => DownloadStatus.Skipped,
            _ => DownloadStatus.Failed
        };
    }
}
