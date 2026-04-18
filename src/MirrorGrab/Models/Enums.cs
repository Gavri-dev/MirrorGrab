namespace MirrorGrab.Models;

public enum DownloadStatus
{
    Queued,
    Extracting,
    Downloading,
    Paused,
    Completed,
    Failed,
    Skipped
}

public enum BatchStatus
{
    Extracting,
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Mixed
}
