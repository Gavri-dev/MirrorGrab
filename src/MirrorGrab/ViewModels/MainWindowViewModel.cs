using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MirrorGrab.Models;
using MirrorGrab.Services;

namespace MirrorGrab.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly Dictionary<int, DownloadBatch> _batches = new();
    private readonly Dictionary<int, DownloadItem> _items = new();
    private readonly Dictionary<int, int> _itemToBatch = new();
    private int _nextBatchId = 1;
    private int _nextItemId = 1;

    private readonly ConcurrentDictionary<int, DownloadEngine> _batchEngines = new();
    private readonly ConcurrentDictionary<int, DownloadEngine> _retryEngines = new();

    public ObservableCollection<BatchViewModel> Batches { get; } = [];

    private readonly Dictionary<int, BatchViewModel> _batchVMs = new();
    private readonly Dictionary<int, DownloadItemViewModel> _itemVMs = new();

    private string _windowTitle = "MirrorGrab";
    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    private string _statsText = "";
    public string StatsText
    {
        get => _statsText;
        set => SetProperty(ref _statsText, value);
    }

    private bool _hasNoBatches = true;
    public bool HasNoBatches
    {
        get => _hasNoBatches;
        set => SetProperty(ref _hasNoBatches, value);
    }

    private int _currentPage;
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public AppSettings Settings { get; private set; } = SettingsService.Load();

    public MainWindowViewModel()
    {
        RestoreState();
    }

    // ---- State persistence ----

    public void SaveState() => SessionStateService.Save(_batches.Values);

    private void RestoreState()
    {
        var saved = SessionStateService.Load();
        var terminal = new HashSet<DownloadStatus>
        {
            DownloadStatus.Completed, DownloadStatus.Skipped, DownloadStatus.Failed
        };

        foreach (var bd in saved)
        {
            if (bd.Items.Count == 0) continue;

            var batch = new DownloadBatch
            {
                Id = _nextBatchId++,
                Name = bd.Name,
                AutoExtract = bd.AutoExtract,
            };

            foreach (var id in bd.Items)
            {
                var status = id.ParseStatus();
                var item = new DownloadItem
                {
                    Id = _nextItemId++,
                    OriginalUrl = id.OriginalUrl,
                    DirectUrl = id.DirectUrl,
                    Filename = id.Filename,
                    Filepath = id.Filepath,
                    DownloadDir = id.DownloadDir,
                    TotalSize = id.TotalSize,
                    DownloadedSize = id.DownloadedSize,
                    Status = status,
                    Error = id.Error,
                };

                if (!terminal.Contains(item.Status))
                {
                    // Try to preserve partial file so the engine can resume via Range requests.
                    // Multipart pre-allocates the full file size, so FileInfo.Length == TotalSize
                    // even mid-download — those files can't be safely resumed, so we drop them.
                    bool canResumeFromDisk = false;
                    if (item.Filepath != null && File.Exists(item.Filepath))
                    {
                        try
                        {
                            var onDisk = new FileInfo(item.Filepath).Length;
                            // Multipart signal: file pre-allocated to full size while downloaded < total.
                            bool preAllocated = item.TotalSize > 0 && onDisk == item.TotalSize
                                                && item.DownloadedSize < item.TotalSize;
                            if (!preAllocated)
                            {
                                item.DownloadedSize = onDisk;
                                canResumeFromDisk = onDisk > 0;
                            }
                            else
                            {
                                try { File.Delete(item.Filepath); } catch { }
                            }
                        }
                        catch { }
                    }

                    if (!canResumeFromDisk)
                    {
                        item.Filepath = null;
                        item.DownloadedSize = 0;
                    }
                    item.Speed = 0;

                    if (item.DirectUrl != null)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.Error = null;
                    }
                    else
                    {
                        // Extraction never finished — can't resume, need to re-extract.
                        // Partial file is useless since its URL may have expired.
                        if (item.Filepath != null && File.Exists(item.Filepath))
                        {
                            try { File.Delete(item.Filepath); } catch { }
                        }
                        item.Filepath = null;
                        item.DownloadedSize = 0;
                        item.Status = DownloadStatus.Failed;
                        item.Error = "Session ended";
                    }
                }

                batch.Items.Add(item);
                _items[item.Id] = item;
                _itemToBatch[item.Id] = batch.Id;
            }

            _batches[batch.Id] = batch;
            var bvm = new BatchViewModel(batch);
            _batchVMs[batch.Id] = bvm;

            foreach (var item in batch.Items)
            {
                var ivm = new DownloadItemViewModel(item);
                _itemVMs[item.Id] = ivm;
                bvm.Items.Add(ivm);
            }

            Batches.Add(bvm);
        }

        SyncUi();
    }

    // ---- Batch lifecycle ----

    public void StartBatch(string name, List<string> urls, bool autoExtract)
    {
        Settings = SettingsService.Load();
        name = UniqueBatchName(name, Settings.DownloadDir);
        var batchDir = Path.Combine(Settings.DownloadDir, name);
        Directory.CreateDirectory(batchDir);

        var batch = new DownloadBatch { Id = _nextBatchId++, Name = name, AutoExtract = autoExtract };
        _batches[batch.Id] = batch;

        var bvm = new BatchViewModel(batch);
        _batchVMs[batch.Id] = bvm;
        Batches.Add(bvm);
        SyncUi();

        // Items are built after a background pass that expands any repack listing pages
        // (e.g. a pasted FitGirl URL) into their individual mirror links.
        _ = Task.Run(() => PrepareBatch(batch, urls, batchDir));
    }

    // Turns raw pasted lines into download items. A FitGirl (repack) page URL is fetched
    // and expanded into the fuckingfast.co links it lists; everything else passes through.
    private async Task PrepareBatch(DownloadBatch batch, List<string> rawUrls, string batchDir)
    {
        var resolved = new List<(string url, string? error)>();

        using (var extractor = new LinkExtractor())
        {
            foreach (var raw in rawUrls)
            {
                var url = raw.Trim();
                if (url.Length == 0) continue;

                if (LinkExtractor.IsRepackPage(url))
                {
                    try
                    {
                        var found = await extractor.ExpandRepackPageAsync(url);
                        if (found.Count > 0)
                            resolved.AddRange(found.Select(f => (f, (string?)null)));
                        else
                            resolved.Add((url, "No fuckingfast.co links found on this page"));
                    }
                    catch (Exception ex)
                    {
                        resolved.Add((url, $"Couldn't read repack page: {ex.Message}"));
                    }
                }
                else
                {
                    resolved.Add((url, (string?)null));
                }
            }
        }

        // De-dup successful links in order (expansion can repeat across pasted pages).
        var seen = new HashSet<string>();
        var final = new List<(string url, string? error)>();
        foreach (var r in resolved)
        {
            if (r.error == null && !seen.Add(r.url.Split('#')[0])) continue;
            final.Add(r);
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (url, error) in final)
            {
                var item = new DownloadItem { Id = _nextItemId++, OriginalUrl = url, DownloadDir = batchDir };
                if (error != null)
                {
                    item.Status = DownloadStatus.Failed;
                    item.Error = error;
                }
                batch.Items.Add(item);
                _items[item.Id] = item;
                _itemToBatch[item.Id] = batch.Id;

                var ivm = new DownloadItemViewModel(item);
                _itemVMs[item.Id] = ivm;
                if (_batchVMs.TryGetValue(batch.Id, out var bvm))
                    bvm.Items.Add(ivm);
            }
            SyncUi();
            _ = Task.Run(() => RunExtraction(batch));
        });
    }

    private string UniqueBatchName(string baseName, string downloadDir)
    {
        var inUse = _batches.Values.Select(b => b.Name).ToHashSet();
        var candidate = baseName;
        int n = 2;
        while (inUse.Contains(candidate) ||
               Directory.Exists(Path.Combine(downloadDir, candidate)))
        {
            candidate = $"{baseName} ({n})";
            n++;
        }
        return candidate;
    }

    private static HttpClient CreateProbeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        BrowserHeaders.Apply(c);
        return c;
    }

    private async Task<bool> ExtractLinkAsync(LinkExtractor extractor, DownloadItem item, int batchId)
    {
        // Already failed during preparation (e.g. a repack page we couldn't scrape).
        if (item.Status == DownloadStatus.Failed && item.Error != null)
            return false;

        item.Status = DownloadStatus.Extracting;
        PostItemUpdate(item.Id);
        PostBatchUpdate(batchId);

        try
        {
            var direct = await extractor.ExtractAsync(item.OriginalUrl);
            if (direct != null)
            {
                item.DirectUrl = direct;
                item.Status = DownloadStatus.Queued;
                PostItemUpdate(item.Id);
                return true;
            }
            item.Status = DownloadStatus.Failed;
            item.Error ??= "Link extraction failed";
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.Error = ex.Message;
        }
        PostItemUpdate(item.Id);
        return false;
    }

    private async Task RunExtraction(DownloadBatch batch)
    {
        using var extractor = new LinkExtractor();
        using var httpClient = CreateProbeClient();

        foreach (var item in batch.Items)
        {
            await ExtractLinkAsync(extractor, item, batch.Id);
            PostBatchUpdate(batch.Id);
        }

        // Probe sizes
        var ready = batch.Items.Where(i => i.DirectUrl != null).ToList();
        if (ready.Count > 0)
        {
            await Task.WhenAll(ready.Select(i => ProbeSize(httpClient, i)));
            foreach (var i in ready)
                PostItemUpdate(i.Id);
        }

        // Check if all failed
        if (!batch.Items.Any(i => i.DirectUrl != null))
        {
            foreach (var item in batch.Items.Where(i => i.Status != DownloadStatus.Failed))
            {
                item.Status = DownloadStatus.Failed;
                item.Error ??= "Link extraction failed";
                PostItemUpdate(item.Id);
            }
            PostBatchUpdate(batch.Id);
            Dispatcher.UIThread.Post(SyncUi);
            return;
        }

        StartDownloads(batch, ready);
    }

    private async Task ProbeSize(HttpClient client, DownloadItem item)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, item.DirectUrl);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (string.IsNullOrEmpty(item.Filename))
            {
                var name = DownloadEngine.ExtractFilename(resp, item.DirectUrl!, fallback: false);
                if (!string.IsNullOrEmpty(name)) item.Filename = name;
            }

            var cl = resp.Content.Headers.ContentLength;
            if (cl.HasValue && cl.Value > 0)
            {
                item.TotalSize = cl.Value;
                return;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"ProbeSize HEAD failed for {item.DirectUrl}: {ex.Message}"); }

        // Range GET fallback
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, item.DirectUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (string.IsNullOrEmpty(item.Filename))
            {
                var name = DownloadEngine.ExtractFilename(resp, item.DirectUrl!, fallback: false);
                if (!string.IsNullOrEmpty(name)) item.Filename = name;
            }

            if (resp.Content.Headers.ContentRange is { } cr && cr.Length.HasValue)
            {
                item.TotalSize = cr.Length.Value;
                return;
            }

            var cl = resp.Content.Headers.ContentLength;
            if (cl.HasValue && cl.Value > 0)
                item.TotalSize = cl.Value;
        }
        catch (Exception ex) { Debug.WriteLine($"ProbeSize Range GET failed for {item.DirectUrl}: {ex.Message}"); }
    }

    private void StartDownloads(DownloadBatch batch, List<DownloadItem> items)
    {
        var settings = SettingsService.Load();
        _ = Task.Run(async () =>
        {
            var engine = new DownloadEngine(settings.DownloadDir, settings.ConcurrentDownloads);
            _batchEngines[batch.Id] = engine;
            engine.OnProgress = item => PostItemProgress(item.Id);
            engine.OnStatusChange = item => PostItemUpdate(item.Id);

            var reHolder = new LinkExtractor?[1];
            var reGate = new SemaphoreSlim(1, 1);
            engine.ReResolve = (item, ct) => ReResolveAsync(item, reHolder, reGate, ct);

            try
            {
                await engine.DownloadAllAsync(items);
            }
            finally
            {
                reHolder[0]?.Dispose();
                reGate.Dispose();
                _batchEngines.TryRemove(batch.Id, out _);
                PostBatchDone(batch.Id);
            }
        });
    }

    // Serialized re-resolver shared across a batch's downloads: only fuckingfast.co page
    // URLs can be refreshed, and only one headless-browser resolve runs at a time.
    private static async Task<string?> ReResolveAsync(
        DownloadItem item, LinkExtractor?[] holder, SemaphoreSlim gate, CancellationToken ct)
    {
        if (!item.OriginalUrl.Contains("fuckingfast.co", StringComparison.OrdinalIgnoreCase))
            return null;

        await gate.WaitAsync(ct);
        try
        {
            holder[0] ??= new LinkExtractor();
            return await holder[0]!.ExtractAsync(item.OriginalUrl);
        }
        finally { gate.Release(); }
    }

    // ---- UI thread dispatch helpers ----

    private void PostItemProgress(int itemId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_itemVMs.TryGetValue(itemId, out var vm))
                vm.Refresh();
            if (_itemToBatch.TryGetValue(itemId, out var bid) && _batchVMs.TryGetValue(bid, out var bvm))
                bvm.Refresh();
            UpdateTitle();
        });
    }

    private void PostItemUpdate(int itemId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_itemVMs.TryGetValue(itemId, out var vm))
                vm.Refresh();
            if (_itemToBatch.TryGetValue(itemId, out var bid) && _batchVMs.TryGetValue(bid, out var bvm))
                bvm.Refresh();
            UpdateTitle();
        });
    }

    private void PostBatchUpdate(int batchId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_batchVMs.TryGetValue(batchId, out var bvm))
                bvm.Refresh();
            SyncUi();
        });
    }

    private void PostBatchDone(int batchId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_batchVMs.TryGetValue(batchId, out var bvm))
                bvm.Refresh();
            SyncUi();

            // Auto-extract
            if (_batches.TryGetValue(batchId, out var batch) && batch.AutoExtract)
            {
                var archives = batch.Items
                    .Where(i => i.Status == DownloadStatus.Completed &&
                                i.Filepath != null && ArchiveService.IsArchive(i.Filepath))
                    .Select(i => i.Filepath!)
                    .ToList();

                if (archives.Count > 0)
                {
                    var groups = ArchiveService.GroupArchives(archives);
                    OnArchiveExtractionNeeded?.Invoke(batch.Name, groups);
                }
            }
        });
    }

    // ---- Actions ----

    public void PauseBatch(int batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch)) return;

        foreach (var item in batch.Items.Where(i => i.Status == DownloadStatus.Downloading))
        {
            var eng = _retryEngines.GetValueOrDefault(item.Id) ??
                      _batchEngines.GetValueOrDefault(batchId);
            if (eng == null) continue;
            eng.Pause(item.Id);
            item.Status = DownloadStatus.Paused;
            PostItemUpdate(item.Id);
        }
    }

    public void ResumeBatch(int batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch)) return;
        var eng = _batchEngines.GetValueOrDefault(batchId);

        foreach (var item in batch.Items.Where(i => i.Status == DownloadStatus.Paused))
        {
            var itemEng = _retryEngines.GetValueOrDefault(item.Id) ?? eng;
            if (itemEng != null)
            {
                itemEng.Resume(item.Id);
                item.Status = DownloadStatus.Downloading;
                PostItemUpdate(item.Id);
            }
        }

        // No engine — restored session
        var resumable = batch.Items
            .Where(i => i.Status == DownloadStatus.Paused && i.DirectUrl != null
                        && !_retryEngines.ContainsKey(i.Id))
            .ToList();
        if (resumable.Count > 0 && eng == null)
        {
            foreach (var i in resumable)
            {
                i.DownloadedSize = 0;
                i.Speed = 0;
                i.StartedAt = null;
                i.FinishedAt = null;
                i.Filepath = null;
                i.Status = DownloadStatus.Queued;
                PostItemUpdate(i.Id);
            }
            StartDownloads(batch, resumable);
        }
    }

    public bool CancelBatch(int batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch)) return false;

        // Cancel the batch engine (kills all downloads + queued items waiting on semaphore)
        var eng = _batchEngines.GetValueOrDefault(batchId);
        eng?.CancelAll();

        var terminal = new HashSet<DownloadStatus>
        {
            DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Skipped
        };

        foreach (var item in batch.Items.Where(i => !terminal.Contains(i.Status)))
        {
            // Also cancel any per-item retry engines
            if (_retryEngines.TryGetValue(item.Id, out var retryEng))
                retryEng.CancelAll();

            item.Status = DownloadStatus.Failed;
            item.Error = "Cancelled";
            PostItemUpdate(item.Id);
        }

        PostBatchUpdate(batchId);
        return true;
    }

    public void RemoveBatch(int batchId)
    {
        if (_batches.Remove(batchId, out var batch))
        {
            foreach (var item in batch.Items)
            {
                _items.Remove(item.Id);
                _itemToBatch.Remove(item.Id);
                _itemVMs.Remove(item.Id);
            }
        }
        _batchVMs.Remove(batchId);

        var bvm = Batches.FirstOrDefault(b => b.Id == batchId);
        if (bvm != null) Batches.Remove(bvm);
        SyncUi();
    }

    public void PauseItem(int itemId)
    {
        if (!_items.TryGetValue(itemId, out var item)) return;
        var eng = _retryEngines.GetValueOrDefault(itemId);
        if (eng == null && _itemToBatch.TryGetValue(itemId, out var bid))
            eng = _batchEngines.GetValueOrDefault(bid);
        if (eng == null) return;

        eng.Pause(itemId);
        item.Status = DownloadStatus.Paused;
        PostItemUpdate(itemId);
    }

    public void ResumeItem(int itemId)
    {
        if (!_items.TryGetValue(itemId, out var item)) return;
        if (item.Status != DownloadStatus.Paused) return;

        var eng = _retryEngines.GetValueOrDefault(itemId);
        if (eng == null && _itemToBatch.TryGetValue(itemId, out var bid))
            eng = _batchEngines.GetValueOrDefault(bid);

        if (eng != null)
        {
            eng.Resume(itemId);
            item.Status = DownloadStatus.Downloading;
            PostItemUpdate(itemId);
        }
        else if (item.DirectUrl != null && _itemToBatch.TryGetValue(itemId, out var batchId))
        {
            ResumeBatch(batchId);
        }
    }

    public void CancelItem(int itemId)
    {
        if (!_items.TryGetValue(itemId, out var item)) return;
        var terminal = new HashSet<DownloadStatus>
        {
            DownloadStatus.Completed, DownloadStatus.Failed, DownloadStatus.Skipped
        };
        if (terminal.Contains(item.Status)) return;

        var eng = _retryEngines.GetValueOrDefault(itemId);
        if (eng == null && _itemToBatch.TryGetValue(itemId, out var bid))
            eng = _batchEngines.GetValueOrDefault(bid);
        eng?.Cancel(itemId);

        item.Status = DownloadStatus.Failed;
        item.Error = "Cancelled";
        PostItemUpdate(itemId);
    }

    public void RemoveItem(int itemId)
    {
        _items.Remove(itemId, out var item);
        _itemToBatch.Remove(itemId, out var batchId);
        _itemVMs.Remove(itemId);

        if (_batches.TryGetValue(batchId, out var batch) && item != null)
            batch.Items.Remove(item);

        if (_batchVMs.TryGetValue(batchId, out var bvm))
        {
            var ivm = bvm.Items.FirstOrDefault(i => i.Id == itemId);
            if (ivm != null) bvm.Items.Remove(ivm);
            bvm.Refresh();
        }

        SyncUi();
    }

    public void RetryItem(int itemId)
    {
        if (!_items.TryGetValue(itemId, out var item)) return;
        if (item.Status != DownloadStatus.Failed && item.Status != DownloadStatus.Skipped) return;
        if (!_itemToBatch.TryGetValue(itemId, out var batchId)) return;
        if (!_batches.TryGetValue(batchId, out var batch)) return;
        if (_retryEngines.ContainsKey(itemId)) return;

        bool hadDirect = item.DirectUrl != null;

        // Keep partial file for resume — set DownloadedSize from actual file.
        // Multipart pre-allocates the full file size, so on-disk Length == TotalSize
        // even mid-download. Those files would falsely appear complete to the engine's
        // resume check, so drop them and start over.
        if (item.Filepath != null && File.Exists(item.Filepath))
        {
            var onDisk = new FileInfo(item.Filepath).Length;
            bool preAllocated = item.TotalSize > 0 && onDisk == item.TotalSize
                                && item.DownloadedSize < item.TotalSize;
            if (preAllocated)
            {
                try { File.Delete(item.Filepath); } catch { }
                item.Filepath = null;
                item.DownloadedSize = 0;
            }
            else
            {
                item.DownloadedSize = onDisk;
            }
        }
        else
        {
            item.DownloadedSize = 0;
            item.Filepath = null;
        }

        item.Status = DownloadStatus.Queued;
        item.Error = null;
        item.Speed = 0;
        item.StartedAt = null;
        item.FinishedAt = null;
        item.UseMultipart = false;
        item.Parts = 0;

        if (!hadDirect)
        {
            item.DirectUrl = null;
            item.TotalSize = 0;
            item.Filename = null;
            item.DownloadedSize = 0;
            item.Filepath = null;
        }

        PostItemUpdate(itemId);
        PostBatchUpdate(batchId);

        _ = Task.Run(() => RunSingleRetry(batch, item, hadDirect));
    }

    public void RetryBatchFailed(int batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch)) return;
        foreach (var item in batch.Items.Where(i => i.Status == DownloadStatus.Failed).ToList())
            RetryItem(item.Id);
    }

    private async Task RunSingleRetry(DownloadBatch batch, DownloadItem item, bool hadDirect)
    {
        try
        {
            // Phase 1: extraction (if needed)
            if (!hadDirect)
            {
                using var extractor = new LinkExtractor();
                if (!await ExtractLinkAsync(extractor, item, batch.Id))
                    return;

                using var httpClient = CreateProbeClient();
                await ProbeSize(httpClient, item);
                PostItemUpdate(item.Id);
            }

            // Phase 2: download
            var settings = SettingsService.Load();
            var engine = new DownloadEngine(settings.DownloadDir, concurrent: 1);
            engine.OnProgress = it => PostItemProgress(it.Id);
            engine.OnStatusChange = it => PostItemUpdate(it.Id);
            _retryEngines[item.Id] = engine;

            var reHolder = new LinkExtractor?[1];
            var reGate = new SemaphoreSlim(1, 1);
            engine.ReResolve = (it, ct) => ReResolveAsync(it, reHolder, reGate, ct);

            try
            {
                await engine.DownloadAllAsync([item]);
            }
            finally
            {
                reHolder[0]?.Dispose();
                reGate.Dispose();
                _retryEngines.TryRemove(item.Id, out _);
                engine.Dispose();
            }
        }
        finally
        {
            PostItemUpdate(item.Id);
            PostBatchUpdate(batch.Id);
        }
    }

    // ---- Context actions ----

    public void OpenItemFolder(int itemId)
    {
        if (!_items.TryGetValue(itemId, out var item)) return;
        if (item.Filepath != null && File.Exists(item.Filepath))
            RevealInExplorer(item.Filepath);
        else if (item.DownloadDir != null && Directory.Exists(item.DownloadDir))
            OpenFolder(item.DownloadDir);
    }

    public void OpenBatchFolder(int batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch)) return;
        var dir = batch.Items.FirstOrDefault()?.DownloadDir;
        if (dir != null && Directory.Exists(dir))
            OpenFolder(dir);
    }

    public string? GetItemUrl(int itemId)
    {
        return _items.TryGetValue(itemId, out var item) ? item.OriginalUrl : null;
    }

    public void OpenItemUrl(int itemId)
    {
        var url = GetItemUrl(itemId);
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Debug.WriteLine($"OpenItemUrl failed for {url}: {ex.Message}"); }
    }

    public bool CanRetryItem(int itemId) =>
        _items.TryGetValue(itemId, out var item) &&
        item.Status is DownloadStatus.Failed or DownloadStatus.Skipped;

    // ---- Toolbar ----

    public void PauseAll()
    {
        foreach (var b in _batches.Values.Where(b =>
            b.Status == BatchStatus.Downloading))
            PauseBatch(b.Id);
    }

    public void ResumeAll()
    {
        foreach (var b in _batches.Values.Where(b =>
            b.Status == BatchStatus.Paused))
            ResumeBatch(b.Id);
    }

    public void ClearFinished()
    {
        var finished = _batches.Values
            .Where(b => b.Status is BatchStatus.Completed or BatchStatus.Failed or BatchStatus.Mixed)
            .Select(b => b.Id).ToList();
        foreach (var id in finished)
            RemoveBatch(id);
    }

    // ---- UI sync ----

    private void SyncUi()
    {
        HasNoBatches = Batches.Count == 0;
        UpdateTitle();
        UpdateStats();
    }

    private void UpdateTitle()
    {
        int active = 0;
        long totalSize = 0, doneSize = 0;
        double speed = 0;

        foreach (var batch in _batches.Values)
        {
            active += batch.Items.Count(i => i.Status == DownloadStatus.Downloading);
            totalSize += batch.TotalSize;
            doneSize += batch.DownloadedSize;
            speed += batch.Speed;
        }

        if (active == 0)
        {
            WindowTitle = "MirrorGrab";
            return;
        }

        var pct = totalSize > 0 ? (int)((double)doneSize / totalSize * 100) : 0;
        var speedTxt = DownloadItem.FormatBytes(speed) + "/s";
        WindowTitle = $"MirrorGrab \u2014 {active} active \u2022 {pct}% \u2022 {speedTxt}";
    }

    private void UpdateStats()
    {
        if (_batches.Count == 0)
        {
            StatsText = "";
            return;
        }

        double speed = _batches.Values.Sum(b => b.Speed);
        var speedTxt = speed > 0 ? $" - {DownloadItem.FormatBytes(speed)}/s" : "";
        StatsText = $"{_batches.Count} batch(es){speedTxt}";
    }

    // ---- Shutdown ----

    public async Task ShutdownAsync()
    {
        SaveState();

        foreach (var (bid, eng) in _batchEngines)
        {
            if (_batches.TryGetValue(bid, out var batch))
            {
                foreach (var item in batch.Items.Where(i =>
                    i.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued))
                {
                    try { eng.Cancel(item.Id); } catch { }
                }
            }
        }

        foreach (var (itemId, eng) in _retryEngines)
        {
            try { eng.Cancel(itemId); } catch { }
        }

        // Grace period
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while ((_batchEngines.Count > 0 || _retryEngines.Count > 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    // ---- Events for view ----

    public event Action<string, List<(string, List<string>)>>? OnArchiveExtractionNeeded;

    // ---- Helpers ----

    private static void OpenFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", path);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", path);
            else
                Process.Start("xdg-open", path);
        }
        catch (Exception ex) { Debug.WriteLine($"OpenFolder failed for {path}: {ex.Message}"); }
    }

    private static void RevealInExplorer(string filepath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,{filepath}");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"-R \"{filepath}\"");
            else
                Process.Start("xdg-open", Path.GetDirectoryName(filepath) ?? ".");
        }
        catch (Exception ex) { Debug.WriteLine($"RevealInExplorer failed for {filepath}: {ex.Message}"); }
    }

    public void ReloadSettings()
    {
        Settings = SettingsService.Load();
    }
}
