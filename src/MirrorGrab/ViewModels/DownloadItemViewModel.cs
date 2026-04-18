using MirrorGrab.Models;

namespace MirrorGrab.ViewModels;

public class DownloadItemViewModel : ViewModelBase
{
    private readonly DownloadItem _model;
    public DownloadItem Model => _model;
    public int Id => _model.Id;

    public DownloadItemViewModel(DownloadItem model)
    {
        _model = model;
    }

    public string DisplayName => _model.Filename ?? ShortUrl(_model.OriginalUrl);
    public string SizeText => _model.FormatSize();
    public double Progress => _model.Progress;
    public string SpeedText => _model.Status == DownloadStatus.Downloading ? _model.FormatSpeed() : "";
    public string EtaText => _model.Status == DownloadStatus.Downloading ? _model.FormatEta() : "";
    public DownloadStatus Status => _model.Status;

    public string StatusText => _model.Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Extracting => "Extracting...",
        DownloadStatus.Downloading => _model.UseMultipart ? "Downloading (16x)" : "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed when _model.Error == "Cancelled" => "Cancelled",
        DownloadStatus.Failed => _model.Error ?? "Failed",
        DownloadStatus.Skipped => "Skipped (exists)",
        _ => ""
    };

    public string StatusColor => _model.Status switch
    {
        DownloadStatus.Completed => "#5bbd5b",
        DownloadStatus.Failed when _model.Error == "Cancelled" => "#e8b84a",
        DownloadStatus.Failed => "#e05858",
        DownloadStatus.Paused => "#e8b84a",
        DownloadStatus.Downloading => "#4a9eff",
        DownloadStatus.Extracting => "#e8b84a",
        DownloadStatus.Skipped => "#8a8a8a",
        _ => "#8a8a8a"
    };

    public string ProgressText => $"{_model.Progress:F0}%";

    public string ProgressBarColor => _model.Status switch
    {
        DownloadStatus.Completed or DownloadStatus.Skipped => "#5bbd5b",
        DownloadStatus.Extracting => "#e8b84a",
        _ => "#4a9eff"
    };

    public bool ShowPause => _model.Status == DownloadStatus.Downloading;
    public bool ShowResume => _model.Status == DownloadStatus.Paused;
    public bool ShowCancel => _model.Status is DownloadStatus.Downloading
        or DownloadStatus.Paused or DownloadStatus.Queued;
    public bool ShowRetry => _model.Status is DownloadStatus.Failed or DownloadStatus.Skipped;
    public bool ShowRemove => _model.Status is DownloadStatus.Failed
        or DownloadStatus.Completed or DownloadStatus.Skipped;

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressBarColor));
        OnPropertyChanged(nameof(ShowPause));
        OnPropertyChanged(nameof(ShowResume));
        OnPropertyChanged(nameof(ShowCancel));
        OnPropertyChanged(nameof(ShowRetry));
        OnPropertyChanged(nameof(ShowRemove));
    }

    private static string ShortUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var seg = uri.Segments.LastOrDefault()?.Trim('/') ?? url;
            return seg.Length > 60 ? seg[..57] + "..." : seg;
        }
        catch { return url.Length > 60 ? url[..57] + "..." : url; }
    }
}
