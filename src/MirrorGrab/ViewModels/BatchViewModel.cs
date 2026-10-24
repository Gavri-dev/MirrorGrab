using System.Collections.ObjectModel;
using MirrorGrab.Models;

namespace MirrorGrab.ViewModels;

public class BatchViewModel : ViewModelBase
{
    private readonly DownloadBatch _model;
    public DownloadBatch Model => _model;
    public int Id => _model.Id;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    public BatchViewModel(DownloadBatch model)
    {
        _model = model;
    }

    public string Name => _model.Name;
    public BatchStatus Status => _model.Status;
    public double Progress => _model.Status == BatchStatus.Extracting
        ? _model.ExtractionRatio
        : _model.Progress;

    public string SizeText
    {
        get
        {
            var status = _model.Status;
            if (status == BatchStatus.Extracting)
                return $"Extracting ({_model.ExtractionRatio:F0}%)";
            if (status is BatchStatus.Downloading or BatchStatus.Paused)
            {
                var done = _model.Items.Count(i =>
                    i.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
                return $"Downloading {done}/{_model.Items.Count}";
            }
            return _model.FormatSize();
        }
    }

    public string SpeedText
    {
        get
        {
            if (_model.Status is BatchStatus.Downloading)
                return _model.FormatSpeed();
            return "";
        }
    }

    public string EtaText
    {
        get
        {
            if (_model.Status is BatchStatus.Downloading)
                return _model.FormatEta();
            return "";
        }
    }

    private bool AllCancelled =>
        _model.Items.Count > 0 &&
        _model.Items.All(i => i.Status == DownloadStatus.Failed && i.Error == "Cancelled");

    public string StatusText => _model.Status switch
    {
        BatchStatus.Extracting => "Extracting",
        BatchStatus.Queued => "Queued",
        BatchStatus.Downloading => "Downloading",
        BatchStatus.Paused => "Paused",
        BatchStatus.Completed => "Completed",
        BatchStatus.Failed when AllCancelled => "Cancelled",
        BatchStatus.Failed => "Failed",
        BatchStatus.Mixed => MixedSummary(),
        _ => ""
    };

    // e.g. "29 ok, 3 cancelled" or "1 ok, 1 failed, 2 cancelled": only the kinds present.
    // Cancelled items are stored as Failed but are reported separately.
    private string MixedSummary()
    {
        int ok = 0, failed = 0, cancelled = 0;
        foreach (var i in _model.Items)
        {
            if (i.Status == DownloadStatus.Completed) ok++;
            else if (i.Status == DownloadStatus.Failed && i.Error == "Cancelled") cancelled++;
            else if (i.Status == DownloadStatus.Failed) failed++;
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add($"{ok} ok");
        if (failed > 0) parts.Add($"{failed} failed");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        return string.Join(", ", parts);
    }

    public string StatusColor => _model.Status switch
    {
        BatchStatus.Completed => "#5bbd5b",
        BatchStatus.Failed when AllCancelled => "#e8b84a",
        BatchStatus.Failed => "#e05858",
        BatchStatus.Paused => "#e8b84a",
        BatchStatus.Downloading => "#4a9eff",
        BatchStatus.Extracting => "#e8b84a",
        _ => "#8a8a8a"
    };

    public string ProgressText => $"{Progress:F0}%";

    public string ProgressBarColor => _model.Status switch
    {
        BatchStatus.Completed => "#5bbd5b",
        BatchStatus.Extracting => "#e8b84a",
        _ => "#4a9eff"
    };

    public bool ShowPause => _model.Status == BatchStatus.Downloading;
    public bool ShowResume => _model.Status == BatchStatus.Paused;
    public bool ShowCancel => _model.Status is BatchStatus.Downloading
        or BatchStatus.Paused or BatchStatus.Extracting;

    public bool ShowRetry
    {
        get
        {
            var s = _model.Status;
            if (s is not (BatchStatus.Failed or BatchStatus.Mixed or BatchStatus.Completed))
                return false;
            return _model.Items.Any(i => i.Status == DownloadStatus.Failed);
        }
    }

    public bool ShowRemove => _model.Status is BatchStatus.Completed
        or BatchStatus.Failed or BatchStatus.Mixed;

    public void Refresh()
    {
        _model.TickSpeed();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
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
}
