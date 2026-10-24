using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MirrorGrab.Services;
using MirrorGrab.ViewModels;

namespace MirrorGrab.Views;

public partial class DownloadsView : UserControl
{
    private MainWindowViewModel VM => (MainWindowViewModel)DataContext!;

    /// <summary>Width of the Name column; the header and every row bind to it.</summary>
    public static readonly StyledProperty<double> NameColumnWidthProperty =
        AvaloniaProperty.Register<DownloadsView, double>(nameof(NameColumnWidth), 300);

    public double NameColumnWidth
    {
        get => GetValue(NameColumnWidthProperty);
        set => SetValue(NameColumnWidthProperty, value);
    }

    private double _nameShare = SettingsService.Load().NameColumnShare;
    private bool _draggingName;
    private double _grabOffset;   // pointer distance from the column edge when the drag began

    public DownloadsView()
    {
        InitializeComponent();
        HeaderGrid.SizeChanged += (_, _) => ApplyNameWidth();
    }

    // ---- Resizable Name column ----

    // Row width shared by Name and the progress bar: everything except the fixed columns.
    private double FlexibleWidth => HeaderGrid.Bounds.Width
        - HeaderGrid.ColumnDefinitions.Where(c => c.Width.IsAbsolute).Sum(c => c.Width.Value);

    private void ApplyNameWidth() =>
        NameColumnWidth = NameColumnLayout.NameWidth(_nameShare, FlexibleWidth);

    private double PointerEdgeX(PointerEventArgs e) =>
        e.GetPosition(HeaderGrid).X - HeaderGrid.ColumnDefinitions[0].ActualWidth;

    private void NameSplitter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _draggingName = true;
        _grabOffset = PointerEdgeX(e) - NameColumnWidth;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void NameSplitter_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingName) return;
        _nameShare = NameColumnLayout.ShareFor(PointerEdgeX(e) - _grabOffset, FlexibleWidth, _nameShare);
        ApplyNameWidth();
    }

    private void NameSplitter_PointerReleased(object? sender, PointerReleasedEventArgs e) =>
        e.Pointer.Capture(null);   // ends the drag via PointerCaptureLost

    private void NameSplitter_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_draggingName) return;
        _draggingName = false;
        var settings = SettingsService.Load();
        settings.NameColumnShare = _nameShare;
        SettingsService.Save(settings);
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

    // ---- Expand/collapse ----

    private const double ExpandMs = 250;
    private readonly HashSet<Border> _movingDrawers = new();

    // The batch's file list slides down from under its row (and back up), pushing the
    // rows below along with it, instead of appearing all at once.
    private void ToggleExpand_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BatchViewModel bvm } btn) return;
        var drawer = btn.FindAncestorOfType<StackPanel>()?.Children
            .OfType<Border>().FirstOrDefault(b => b.Classes.Contains("batchItems"));
        if (drawer?.Child is not Control list)
        {
            bvm.IsExpanded = !bvm.IsExpanded;
            return;
        }
        if (!_movingDrawers.Add(drawer)) return;   // already animating

        if (bvm.IsExpanded)
        {
            AnimateDrawer(drawer, list, drawer.Bounds.Height, 0, () => bvm.IsExpanded = false);
        }
        else
        {
            bvm.IsExpanded = true;
            var width = drawer.Parent is Control parent ? parent.Bounds.Width : Bounds.Width;
            drawer.Measure(new Size(width, double.PositiveInfinity));
            AnimateDrawer(drawer, list, 0, drawer.DesiredSize.Height, null);
        }
    }

    private void AnimateDrawer(Border drawer, Control list, double from, double to, Action? done)
    {
        var top = TopLevel.GetTopLevel(this);
        var ease = new CubicEaseOut();
        var full = Math.Max(from, to);
        var slide = new TranslateTransform();
        list.RenderTransform = slide;
        TimeSpan? start = null;

        void Frame(TimeSpan now)
        {
            start ??= now;
            var t = Math.Min(1, (now - start.Value).TotalMilliseconds / ExpandMs);
            var h = from + (to - from) * ease.Ease(t);
            drawer.Height = h;
            slide.Y = h - full;                       // list hangs from the drawer's bottom edge
            list.Opacity = full > 0 ? h / full : 1;
            if (t < 1 && top != null)
            {
                top.RequestAnimationFrame(Frame);
                return;
            }

            done?.Invoke();
            drawer.Height = double.NaN;
            list.RenderTransform = null;
            list.Opacity = 1;
            _movingDrawers.Remove(drawer);
        }

        if (top != null) top.RequestAnimationFrame(Frame);
        else Frame(TimeSpan.Zero);
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
