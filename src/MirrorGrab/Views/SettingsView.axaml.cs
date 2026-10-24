using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MirrorGrab.Services;

namespace MirrorGrab.Views;

public partial class SettingsView : UserControl
{
    private AppSettings _settings;
    public event Action? SettingsChanged;

    public SettingsView()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        LoadSettingsToUi();
    }

    private void LoadSettingsToUi()
    {
        FolderLabel.Text = _settings.DownloadDir;
        ConcurrentSpin.Value = _settings.ConcurrentDownloads;
        AutoExtractToggle.IsChecked = _settings.AutoExtract;
        TrayToggle.IsChecked = _settings.CloseToTray;
    }

    private void Save()
    {
        // The Downloads view saves the column split itself; don't overwrite it with
        // the value we loaded at startup.
        _settings.NameColumnShare = SettingsService.Load().NameColumnShare;
        SettingsService.Save(_settings);
        SettingsChanged?.Invoke();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var storage = window.StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Download Folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path == null) return;

        _settings.DownloadDir = path;
        FolderLabel.Text = path;
        Save();
    }

    private void Concurrent_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.NewValue.HasValue)
        {
            _settings.ConcurrentDownloads = (int)e.NewValue.Value;
            Save();
        }
    }

    private void AutoExtract_Changed(object? sender, RoutedEventArgs e)
    {
        _settings.AutoExtract = AutoExtractToggle.IsChecked == true;
        Save();
    }

    private void Tray_Changed(object? sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = TrayToggle.IsChecked == true;
        Save();
    }
}
