using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MirrorGrab.ViewModels;

namespace MirrorGrab.Views;

public partial class DownloadsView : UserControl
{
    private MainWindowViewModel VM => (MainWindowViewModel)DataContext!;

    public DownloadsView()
    {
        InitializeComponent();
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var dlg = new AddDialog();
        var result = await dlg.ShowDialog<bool?>(window);
        if (result == true)
        {
            VM.StartBatch(dlg.GetName(), dlg.GetUrls(), dlg.GetAutoExtract());
        }
    }

    private void PauseAll_Click(object? sender, RoutedEventArgs e) => VM.PauseAll();
    private void ResumeAll_Click(object? sender, RoutedEventArgs e) => VM.ResumeAll();
    private void ClearFinished_Click(object? sender, RoutedEventArgs e) => VM.ClearFinished();

    private void ToggleExpand_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BatchViewModel bvm)
            bvm.IsExpanded = !bvm.IsExpanded;
    }

    // ---- Batch actions ----

    private void PauseBatch_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.PauseBatch(id);
    }

    private void ResumeBatch_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.ResumeBatch(id);
    }

    private async void CancelBatch_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is not int id) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        var confirmed = await MessageBox.Confirm(window, "Cancel Batch",
            "Cancel this batch? Active downloads will stop and partial data will be discarded.");
        if (confirmed)
            VM.CancelBatch(id);
    }

    private void RetryBatch_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.RetryBatchFailed(id);
    }

    private void RemoveBatch_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.RemoveBatch(id);
    }

    private void OpenBatchFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.OpenBatchFolder(id);
    }

    // ---- Item actions ----

    private void PauseItem_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.PauseItem(id);
    }

    private void ResumeItem_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.ResumeItem(id);
    }

    private void CancelItem_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.CancelItem(id);
    }

    private void RetryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.RetryItem(id);
    }

    private void RemoveItem_Click(object? sender, RoutedEventArgs e)
    {
        if (GetId(sender) is int id) VM.RemoveItem(id);
    }

    private static int? GetId(object? sender)
    {
        if (sender is Button btn && btn.Tag is int id)
            return id;
        return null;
    }

    // ---- Context menu handlers ----

    private static int? GetMenuId(object? sender)
    {
        if (sender is MenuItem mi && mi.Tag is int id)
            return id;
        return null;
    }

    private void Ctx_OpenBatchFolder(object? sender, RoutedEventArgs e)
    {
        if (GetMenuId(sender) is int id) VM.OpenBatchFolder(id);
    }

    private void Ctx_OpenItemFolder(object? sender, RoutedEventArgs e)
    {
        if (GetMenuId(sender) is int id) VM.OpenItemFolder(id);
    }

    private async void Ctx_CopyUrl(object? sender, RoutedEventArgs e)
    {
        if (GetMenuId(sender) is not int id) return;
        var url = VM.GetItemUrl(id);
        if (string.IsNullOrEmpty(url)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(url);
    }

    private void Ctx_OpenUrl(object? sender, RoutedEventArgs e)
    {
        if (GetMenuId(sender) is int id) VM.OpenItemUrl(id);
    }

    private void Ctx_Retry(object? sender, RoutedEventArgs e)
    {
        if (GetMenuId(sender) is int id) VM.RetryItem(id);
    }
}
