using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MirrorGrab.Services;

namespace MirrorGrab.Views;

public partial class AddDialog : Window
{
    public AddDialog()
    {
        InitializeComponent();
        AutoExtractToggle.IsChecked = SettingsService.Load().AutoExtract;   // Settings default
    }

    public void Preset(List<string> urls, string suggestedName = "")
    {
        if (!string.IsNullOrWhiteSpace(suggestedName))
            NameEdit.Text = suggestedName;
        UrlsEdit.Text = string.Join("\n", urls);
    }

    public string GetName()
    {
        var name = Sanitize(NameEdit.Text?.Trim() ?? "");
        return string.IsNullOrWhiteSpace(name) ? "Downloads" : name;
    }

    public bool GetAutoExtract() => AutoExtractToggle.IsChecked == true;

    public List<string> GetUrls()
    {
        var text = UrlsEdit.Text ?? "";
        return Regex.Matches(text, @"(https?://[^\s]+)")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameEdit.Text))
        {
            NameEdit.Focus();
            return;
        }
        if (GetUrls().Count == 0)
        {
            UrlsEdit.Focus();
            return;
        }
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load URLs from Text File",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });

        if (files.Count == 0) return;
        var file = files[0];

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        UrlsEdit.Text = content;

        if (string.IsNullOrWhiteSpace(NameEdit.Text))
            NameEdit.Text = Path.GetFileNameWithoutExtension(file.Name);
    }

    private static string Sanitize(string name)
    {
        name = Regex.Replace(name, @"[^\w\s.\-_()[\]]", "");
        name = name.TrimStart('.');
        return name;
    }
}
