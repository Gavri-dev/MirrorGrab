using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MirrorGrab.Services;
using MirrorGrab.ViewModels;
using System.Text.RegularExpressions;

namespace MirrorGrab.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private bool _forceQuit = false;

    public void ForceQuit() => _forceQuit = true;

    private static readonly SolidColorBrush SelectedBg = SolidColorBrush.Parse("#2d4f7a");
    private static readonly SolidColorBrush TransparentBg = new(Colors.Transparent);
    private static readonly SolidColorBrush AccentFg = SolidColorBrush.Parse("#4a9eff");
    private static readonly SolidColorBrush DimFg = SolidColorBrush.Parse("#8a8a8a");

    public MainWindow()
    {
        _vm = new MainWindowViewModel();
        DataContext = _vm;

        InitializeComponent();

        DownloadsPage.DataContext = _vm;
        SettingsPage.SettingsChanged += () => _vm.ReloadSettings();
        _vm.OnArchiveExtractionNeeded += (batchName, groups) =>
            new ExtractionDialog(batchName, groups).Show(this);

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);

        SetNavActive(NavDownloads);
    }

    private void NavDownloads_Click(object? sender, RoutedEventArgs e)
    {
        DownloadsPage.IsVisible = true;
        SettingsPage.IsVisible = false;
        SetNavActive(NavDownloads);
        SetNavInactive(NavSettings);
    }

    private void NavSettings_Click(object? sender, RoutedEventArgs e)
    {
        DownloadsPage.IsVisible = false;
        SettingsPage.IsVisible = true;
        SetNavActive(NavSettings);
        SetNavInactive(NavDownloads);
    }

    private void SetNavActive(Button btn)
    {
        btn.Background = SelectedBg;
        btn.Foreground = AccentFg;
        foreach (var tb in btn.GetVisualDescendants().OfType<TextBlock>())
            tb.Foreground = AccentFg;
        foreach (var icon in btn.GetVisualDescendants().OfType<PathIcon>())
            icon.Foreground = AccentFg;
    }

    private void SetNavInactive(Button btn)
    {
        btn.Background = TransparentBg;
        btn.Foreground = DimFg;
        foreach (var tb in btn.GetVisualDescendants().OfType<TextBlock>())
            tb.Foreground = DimFg;
        foreach (var icon in btn.GetVisualDescendants().OfType<PathIcon>())
            icon.Foreground = DimFg;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null && files.Any(f =>
                f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                e.DragEffects = DragDropEffects.Copy;
                return;
            }
        }
        e.DragEffects = DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // async void: an unhandled exception here would crash the app.
        try
        {
            if (!e.Data.Contains(DataFormats.Files)) return;
            var files = e.Data.GetFiles()?.ToList();
            if (files == null) return;

            foreach (var file in files)
            {
                if (!file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                var path = (file as IStorageFile)?.TryGetLocalPath();
                if (path == null || !File.Exists(path)) continue;

                var raw = await File.ReadAllTextAsync(path);
                var urls = Regex.Matches(raw, @"(https?://[^\s]+)")
                    .Select(m => m.Groups[1].Value).ToList();

                if (urls.Count == 0)
                {
                    await MessageBox.Show(this, "No Links",
                        $"No valid URLs found in '{Path.GetFileName(path)}'.");
                    return;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                var dlg = new AddDialog();
                dlg.Preset(urls, name);

                var result = await dlg.ShowDialog<bool?>(this);
                if (result == true)
                {
                    _vm.StartBatch(dlg.GetName(), dlg.GetUrls(), dlg.GetAutoExtract());
                }
                return;
            }
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Drop Failed", $"Could not process dropped file: {ex.Message}");
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // async void override: swallow exceptions so a shutdown error doesn't kill the app.
        try
        {
            var settings = SettingsService.Load();
            if (settings.CloseToTray && !_forceQuit)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            await _vm.ShutdownAsync();
            base.OnClosing(e);
        }
        catch
        {
            base.OnClosing(e);
        }
    }
}

public static class MessageBox
{
    public static async Task<bool> Show(Window parent, string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = SolidColorBrush.Parse("#2a2a2a"),
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = SolidColorBrush.Parse("#e4e4e4"),
        });

        var btn = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        btn.Click += (_, _) => dlg.Close();
        panel.Children.Add(btn);

        dlg.Content = panel;
        await dlg.ShowDialog(parent);
        return true;
    }

    public static async Task<bool> Confirm(Window parent, string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 450,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = SolidColorBrush.Parse("#2a2a2a"),
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = SolidColorBrush.Parse("#e4e4e4"),
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };

        var noBtn = new Button { Content = "No" };
        noBtn.Click += (_, _) => dlg.Close(false);
        buttons.Children.Add(noBtn);

        var yesBtn = new Button { Content = "Yes" };
        yesBtn.Click += (_, _) => dlg.Close(true);
        buttons.Children.Add(yesBtn);

        panel.Children.Add(buttons);
        dlg.Content = panel;

        var result = await dlg.ShowDialog<object?>(parent);
        return result is true;
    }
}
