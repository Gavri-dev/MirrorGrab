namespace MirrorGrab.Models;

public class DownloadBatch
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool AutoExtract { get; set; }
    public List<DownloadItem> Items { get; set; } = [];
    public double CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private double _smoothedSpeed;
    private double _lastTick;
    private const double SmoothTau = 6.0;

    /// <summary>Auto-extract has been started for this batch (once per session).</summary>
    public bool AutoExtractStarted { get; set; }

    // Cancelled and skipped items aren't being downloaded, so they're left out of the
    // batch's size, progress, ETA and auto-extract. Retrying one puts it back in.
    public IEnumerable<DownloadItem> CountedItems => Items.Where(i =>
        i.Status != DownloadStatus.Skipped &&
        !(i.Status == DownloadStatus.Failed && i.Error == "Cancelled"));

    public long TotalSize => CountedItems.Sum(i => i.TotalSize);
    public long DownloadedSize => CountedItems.Sum(i => i.DownloadedSize);

    public double Progress =>
        TotalSize > 0 ? (double)DownloadedSize / TotalSize * 100.0 : 0;

    public double RawSpeed =>
        Items.Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.Speed);

    public double Speed => _smoothedSpeed;

    public double? EtaSeconds
    {
        get
        {
            if (_smoothedSpeed <= 0 || TotalSize <= 0) return null;
            var remaining = TotalSize - DownloadedSize;
            return remaining / _smoothedSpeed;
        }
    }

    public double ExtractionRatio
    {
        get
        {
            if (Items.Count == 0) return 0;
            // Only count items that have actually been processed:
            // they either got a direct URL (success) or failed
            var done = Items.Count(i => i.DirectUrl != null || i.Status == DownloadStatus.Failed);
            return (double)done / Items.Count * 100.0;
        }
    }

    public BatchStatus Status
    {
        get
        {
            if (Items.Count == 0) return BatchStatus.Queued;
            var statuses = Items.Select(i => i.Status).ToHashSet();

            if (statuses.All(s => s == DownloadStatus.Extracting))
                return BatchStatus.Extracting;
            if (statuses.Any(s => s == DownloadStatus.Extracting))
                return BatchStatus.Extracting;
            if (statuses.All(s => s == DownloadStatus.Queued))
                return BatchStatus.Queued;
            if (statuses.Any(s => s == DownloadStatus.Downloading))
                return BatchStatus.Downloading;

            var pauseAllowed = new HashSet<DownloadStatus>
            {
                DownloadStatus.Paused, DownloadStatus.Queued,
                DownloadStatus.Completed, DownloadStatus.Failed,
                DownloadStatus.Skipped
            };
            if (statuses.All(s => pauseAllowed.Contains(s)) &&
                statuses.Contains(DownloadStatus.Paused))
                return BatchStatus.Paused;

            var terminal = new HashSet<DownloadStatus>
            {
                DownloadStatus.Completed, DownloadStatus.Failed,
                DownloadStatus.Skipped
            };
            if (statuses.All(s => terminal.Contains(s)))
            {
                if (statuses.Contains(DownloadStatus.Failed) &&
                    !statuses.Contains(DownloadStatus.Completed))
                    return BatchStatus.Failed;
                if (!statuses.Contains(DownloadStatus.Failed))
                    return BatchStatus.Completed;
                return BatchStatus.Mixed;
            }

            // Anything left is waiting to start (e.g. one link failed while the rest are
            // still being probed). Mixed would count as finished and let it be removed.
            return BatchStatus.Queued;
        }
    }

    public void TickSpeed()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var raw = RawSpeed;

        if (_lastTick == 0 || (now - _lastTick) > 4.0)
        {
            _smoothedSpeed = raw;
            _lastTick = now;
            return;
        }

        var dt = now - _lastTick;
        _lastTick = now;
        var alpha = 1.0 - Math.Exp(-dt / SmoothTau);
        _smoothedSpeed += alpha * (raw - _smoothedSpeed);
    }

    public string FormatSize() => DownloadItem.FormatBytes(TotalSize);
    public string FormatSpeed() => $"{DownloadItem.FormatBytes(_smoothedSpeed)}/s";
    public string FormatEta() => EtaSeconds.HasValue ? DownloadItem.FormatSeconds(EtaSeconds.Value) : "";
}
