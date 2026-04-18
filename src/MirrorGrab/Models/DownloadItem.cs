namespace MirrorGrab.Models;

public class DownloadItem
{
    public int Id { get; set; }
    public string OriginalUrl { get; set; } = "";
    public string? DirectUrl { get; set; }
    public string? Filename { get; set; }
    public string? Filepath { get; set; }
    public string? DownloadDir { get; set; }
    public long TotalSize { get; set; }
    public long DownloadedSize { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    public double Speed { get; set; }
    public int Parts { get; set; }
    public bool UseMultipart { get; set; }
    public double? StartedAt { get; set; }
    public double? FinishedAt { get; set; }

    public double Progress =>
        TotalSize > 0 ? (double)DownloadedSize / TotalSize * 100.0 : 0;

    public double? EtaSeconds
    {
        get
        {
            if (Speed <= 0 || TotalSize <= 0) return null;
            var remaining = TotalSize - DownloadedSize;
            return remaining / Speed;
        }
    }

    public string FormatSpeed() => $"{FormatBytes(Speed)}/s";

    public string FormatSize()
    {
        if (TotalSize <= 0)
            return DownloadedSize > 0 ? FormatBytes(DownloadedSize) : "";
        return $"{FormatBytes(DownloadedSize)} / {FormatBytes(TotalSize)}";
    }

    public string FormatEta()
    {
        var eta = EtaSeconds;
        if (eta == null || eta <= 0) return "";
        return FormatSeconds(eta.Value);
    }

    public static string FormatBytes(double size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var s = Math.Abs(size);
        foreach (var u in units)
        {
            if (s < 1024) return $"{s:F1} {u}";
            s /= 1024;
        }
        return $"{s:F1} PB";
    }

    public static string FormatSeconds(double seconds)
    {
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600)
        {
            var m = (int)(seconds / 60);
            var s = (int)(seconds % 60);
            return $"{m}m {s:D2}s";
        }
        var h = (int)(seconds / 3600);
        var mm = (int)((seconds % 3600) / 60);
        return $"{h}h {mm:D2}m";
    }
}
