using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MirrorGrab.Services;

namespace MirrorGrab.Views;

public partial class ExtractionDialog : Window
{
    private readonly List<(string First, List<string> Parts)> _groups;
    private readonly List<string> _successFiles = [];
    private readonly List<string> _failedFiles = [];
    private int _doneCount;
    private bool _finished;

    // Parameterless ctor for XAML loader / designer — must not start work.
    public ExtractionDialog()
    {
        InitializeComponent();
        _groups = [];
        _finished = true;
    }

    public ExtractionDialog(string batchName, List<(string First, List<string> Parts)> groups) : this()
    {
        _groups = groups;
        _finished = false;

        TitleLabel.Text = $"Extracting archives from '{batchName}'";
        OverallProgress.Maximum = groups.Count;
        OverallProgress.Value = 0;

        foreach (var (first, _) in groups)
        {
            FileList.Items.Add(Path.GetFileName(first) + " — waiting...");
        }

        _ = Task.Run(RunExtraction);
    }

    private Task RunExtraction()
    {
        for (int i = 0; i < _groups.Count; i++)
        {
            var (first, parts) = _groups[i];
            var name = Path.GetFileName(first);
            // The posted updates run later, after the loop has moved on: give each one its
            // own copies instead of the shared loop variable and counter.
            var row = i;
            var position = _doneCount + 1;

            Dispatcher.UIThread.Post(() =>
            {
                CurrentLabel.Text = $"[{position}/{_groups.Count}] {name}";
                if (row < FileList.Items.Count)
                    FileList.Items[row] = $"{name} — extracting...";
            });

            try
            {
                ArchiveService.Extract(first);

                Dispatcher.UIThread.Post(() =>
                {
                    if (row < FileList.Items.Count)
                        FileList.Items[row] = $"\u2713 {name}";
                });

                _successFiles.AddRange(parts);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (row < FileList.Items.Count)
                        FileList.Items[row] = $"\u2717 {name} — {ex.Message}";
                });

                _failedFiles.Add(first);
            }

            var done = ++_doneCount;
            Dispatcher.UIThread.Post(() =>
            {
                OverallProgress.Value = done;
            });
        }

        _finished = true;
        Dispatcher.UIThread.Post(() =>
        {
            var msg = $"Extracted {_groups.Count - _failedFiles.Count} archive(s).";   // archives, not part files
            if (_failedFiles.Count > 0)
                msg += $" {_failedFiles.Count} failed.";
            SummaryLabel.Text = msg;
            CurrentLabel.Text = "Done";
            CloseBtn.Content = "Close";
            if (_successFiles.Count > 0)
                DeleteBtn.IsVisible = true;
        });

        return Task.CompletedTask;
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageBox.Confirm(this, "Delete Source Archives",
            $"Delete {_successFiles.Count} source archive file(s)?");
        if (!confirmed) return;

        int deleted = 0;
        foreach (var f in _successFiles)
        {
            try
            {
                if (File.Exists(f)) { File.Delete(f); deleted++; }
            }
            catch { }
        }

        SummaryLabel.Text += $" Deleted {deleted} file(s).";
        DeleteBtn.IsVisible = false;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (_finished) Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_finished) e.Cancel = true;
        base.OnClosing(e);
    }
}
