using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using MirrorGrab.Models;

namespace MirrorGrab.Services;

public class DownloadEngine : IDisposable
{
    private const int PartsPerFile = 16;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int WriteBuffer = 8 * 1024 * 1024;
    private const int MaxRetries = 3;
    private const double ProgressInterval = 0.5;

    private static readonly HashSet<string> StrictHosts =
        ["fuckingfast.co", "datanodes.to"];

    private static readonly object FilenameLock = new();

    private readonly string _downloadDir;
    private readonly int _concurrent;
    private readonly SemaphoreSlim _semaphore;
    private HttpClient? _client;

    private CancellationTokenSource? _batchCts;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cancelTokens = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _pauseTcs = new();

    public Action<DownloadItem>? OnProgress;
    public Action<DownloadItem>? OnStatusChange;
    public Action<string>? OnLog;

    /// <summary>
    /// Optional: given an item whose direct link was rejected (expired signed URL),
    /// produce a fresh direct URL. Return null if the item can't be re-resolved.
    /// Used to recover fuckingfast.co links that time out while queued or mid-download.
    /// </summary>
    public Func<DownloadItem, CancellationToken, Task<string?>>? ReResolve;

    public DownloadEngine(string downloadDir, int concurrent = 8)
    {
        _downloadDir = downloadDir;
        _concurrent = concurrent;
        _semaphore = new SemaphoreSlim(concurrent);
    }

    public async Task DownloadAllAsync(IReadOnlyList<DownloadItem> items)
    {
        _batchCts = new CancellationTokenSource();
        _client = CreateClient();
        try
        {
            var tasks = items.Select(DownloadWithSemAsync);
            await Task.WhenAll(tasks);
        }
        finally
        {
            _client.Dispose();
            _client = null;
        }
    }

    public void CancelAll()
    {
        _batchCts?.Cancel();
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _concurrent * PartsPerFile,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        BrowserHeaders.Apply(client);
        return client;
    }

    public void Pause(int itemId)
    {
        _pauseTcs.TryAdd(itemId, new TaskCompletionSource());
    }

    public void Resume(int itemId)
    {
        if (_pauseTcs.TryRemove(itemId, out var tcs))
            tcs.TrySetResult();
    }

    public void Cancel(int itemId)
    {
        if (_cancelTokens.TryGetValue(itemId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { } // item finished as we cancelled it
        }
        // Also wake from pause so the loop can exit
        if (_pauseTcs.TryRemove(itemId, out var tcs))
            tcs.TrySetResult();
    }

    private async Task DownloadWithSemAsync(DownloadItem item)
    {
        // Cancelled before it reached the engine (e.g. during link extraction or size probe).
        if (item.Status == DownloadStatus.Failed && item.Error == "Cancelled") return;

        // Register the item's token before queueing, so Cancel() also works while it waits.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_batchCts!.Token);
        _cancelTokens[item.Id] = cts;
        try
        {
            try
            {
                await _semaphore.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // Item or batch cancelled while waiting in queue
            }

            try
            {
                if (cts.IsCancellationRequested) return;
                await DownloadFileAsync(item, cts.Token);
            }
            finally
            {
                ReleaseActive(item.Filepath);
                _semaphore.Release();
            }
        }
        finally
        {
            _cancelTokens.TryRemove(item.Id, out _);
        }
    }

    private async Task DownloadFileAsync(DownloadItem item, CancellationToken ct)
    {
        if (item.Filepath != null) MarkActive(item.Filepath);
        bool singleStreamOnly = false;   // set once a server is caught ignoring part ranges

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (ct.IsCancellationRequested) break;

                item.Status = DownloadStatus.Downloading;
                item.StartedAt ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                OnStatusChange?.Invoke(item);

                // A multipart file is pre-allocated to full size, so its length says nothing
                // about what arrived: after a failed multipart attempt, start the file over.
                if (item.UseMultipart)
                {
                    ResetPartial(item);
                    item.UseMultipart = false;
                }

                var url = item.DirectUrl ?? item.OriginalUrl;

                // Check for resume: existing partial file
                long resumeFrom = 0;
                if (item.Filepath != null && File.Exists(item.Filepath))
                {
                    resumeFrom = new FileInfo(item.Filepath).Length;
                    if (resumeFrom > 0 && item.TotalSize > 0 && resumeFrom >= item.TotalSize)
                    {
                        // Already complete
                        item.DownloadedSize = item.TotalSize;
                        item.Status = DownloadStatus.Completed;
                        item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        item.Speed = 0;
                        OnStatusChange?.Invoke(item);
                        return;
                    }
                }

                // Build request with Range header for resume
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (resumeFrom > 0)
                    request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

                using var resp = await _client!.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                // Extract filename
                if (string.IsNullOrEmpty(item.Filename))
                    item.Filename = ExtractFilename(resp, url);

                // Claim filepath if not already set
                if (item.Filepath == null)
                {
                    var destDir = item.DownloadDir ?? _downloadDir;
                    Directory.CreateDirectory(destDir);

                    var claimedPath = ClaimFilepath(destDir, item.Filename ?? $"download_{item.Id}");
                    if (claimedPath == null)
                    {
                        item.Status = DownloadStatus.Skipped;
                        item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        OnStatusChange?.Invoke(item);
                        return;
                    }
                    item.Filepath = claimedPath;
                }

                // Handle response: partial (206) vs full (200)
                bool resuming = false;
                if (resp.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0)
                {
                    // Server supports Range, resuming
                    var cr = resp.Content.Headers.ContentRange;
                    if (cr?.Length != null)
                        item.TotalSize = cr.Length.Value;
                    item.DownloadedSize = resumeFrom;
                    resuming = true;
                }
                else
                {
                    // Full content — server ignored Range or fresh start
                    var contentLength = resp.Content.Headers.ContentLength;
                    if (contentLength.HasValue && contentLength.Value > 0)
                        item.TotalSize = contentLength.Value;
                    resumeFrom = 0;
                    item.DownloadedSize = 0;
                    resuming = false;
                }

                // Only use multipart for fresh starts (not resume)
                if (!resuming)
                {
                    var host = new Uri(url).Host.ToLower();
                    var acceptRanges = resp.Headers.Contains("Accept-Ranges") &&
                        resp.Headers.GetValues("Accept-Ranges").Any(v => v == "bytes");
                    var canMultipart = acceptRanges && item.TotalSize > 0 &&
                        !StrictHosts.Any(h => host.Contains(h));

                    if (canMultipart && !singleStreamOnly && item.TotalSize > ChunkSize * 2)
                    {
                        item.UseMultipart = true;
                        item.Parts = PartsPerFile;
                        resp.Dispose();
                        await MultipartDownloadAsync(item, url, ct);

                        item.Status = DownloadStatus.Completed;
                        item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        item.Speed = 0;
                        OnStatusChange?.Invoke(item);
                        return;
                    }
                }

                item.UseMultipart = false;
                await SingleDownloadAsync(item, resp, resuming, ct);

                item.Status = DownloadStatus.Completed;
                item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                item.Speed = 0;
                OnStatusChange?.Invoke(item);
                return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Attempt {attempt + 1} failed for {item.Filename}: {ex.Message}");

                if (ex is RangeNotHonoredException && attempt < MaxRetries - 1)
                {
                    OnLog?.Invoke($"{item.Filename}: server ignored byte ranges; downloading as one stream.");
                    singleStreamOnly = true;
                    continue;
                }

                // Signed mirror links (fuckingfast.co) expire. On a 403/410/etc, ask the
                // host to hand us a fresh link and retry from scratch with it.
                if (IsExpiredLink(ex) && ReResolve != null && attempt < MaxRetries - 1
                    && !ct.IsCancellationRequested)
                {
                    OnLog?.Invoke($"Link for {item.Filename} looks expired — re-resolving…");
                    try
                    {
                        var fresh = await ReResolve(item, ct);
                        if (ct.IsCancellationRequested) break;
                        if (!string.IsNullOrWhiteSpace(fresh))
                        {
                            item.DirectUrl = fresh;
                            ResetPartial(item);   // fresh link ⇒ start the file clean
                            OnLog?.Invoke($"Got a fresh link for {item.Filename}; retrying.");
                            continue;             // retry now, no backoff
                        }
                        OnLog?.Invoke($"Re-resolve produced no link for {item.Filename}.");
                    }
                    catch (Exception rex)
                    {
                        OnLog?.Invoke($"Re-resolve failed for {item.Filename}: {rex.Message}");
                    }
                }

                if (attempt == MaxRetries - 1 && !ct.IsCancellationRequested)
                {
                    item.Error = ex.Message;
                }
                else if (attempt < MaxRetries - 1)
                {
                    try { await Task.Delay(1000 * (attempt + 1), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // If we reach here, download failed or was cancelled
        if (item.Status != DownloadStatus.Completed && item.Status != DownloadStatus.Skipped)
        {
            item.Status = DownloadStatus.Failed;
            if (ct.IsCancellationRequested)
                item.Error = "Cancelled";
            else
                item.Error ??= "Unknown error";
            item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            item.Speed = 0;
            OnStatusChange?.Invoke(item);
        }
    }

    // A rejected signed link surfaces as an HTTP error status on the GET (initial request
    // or a multipart part). Treat the usual "link no longer valid" codes as expiry.
    private static bool IsExpiredLink(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is HttpRequestException hre && hre.StatusCode is
                HttpStatusCode.Forbidden or HttpStatusCode.Gone or
                HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                return true;
        return false;
    }

    // Discard any bytes already on disk so a re-resolved link restarts the file cleanly.
    private void ResetPartial(DownloadItem item)
    {
        item.DownloadedSize = 0;
        item.Speed = 0;
        try
        {
            if (item.Filepath != null && File.Exists(item.Filepath))
                File.WriteAllBytes(item.Filepath, []);
        }
        catch (Exception ex) { OnLog?.Invoke($"Could not reset {item.Filepath}: {ex.Message}"); }
    }

    private async Task SingleDownloadAsync(DownloadItem item, HttpResponseMessage resp,
        bool resume, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(item.Filepath!,
            resume ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, WriteBuffer, useAsync: true);

        var buffer = new byte[ChunkSize];
        var lastProgress = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var lastBytes = item.DownloadedSize;

        while (true)
        {
            await WaitIfPausedAsync(item.Id, ct);
            ct.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            item.DownloadedSize += read;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (now - lastProgress >= ProgressInterval)
            {
                var dt = now - lastProgress;
                item.Speed = (item.DownloadedSize - lastBytes) / dt;
                lastBytes = item.DownloadedSize;
                lastProgress = now;
                OnProgress?.Invoke(item);
            }
        }

        OnProgress?.Invoke(item);
    }

    private async Task MultipartDownloadAsync(DownloadItem item, string url,
        CancellationToken ct)
    {
        // Pre-allocate file
        await using (var fs = new FileStream(item.Filepath!, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(item.TotalSize);
        }

        var partSize = item.TotalSize / PartsPerFile;
        var progress = new long[PartsPerFile];
        var lastProgressTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var lastBytes = 0L;

        var tasks = new Task[PartsPerFile];
        for (int i = 0; i < PartsPerFile; i++)
        {
            var start = i * partSize;
            var end = (i == PartsPerFile - 1) ? item.TotalSize - 1 : (i + 1) * partSize - 1;
            var idx = i;

            tasks[i] = Task.Run(async () =>
            {
                await DownloadPartAsync(item, url, start, end, progress, idx, ct);
            }, ct);
        }

        // Progress reporting loop — stopped when parts finish (or on cancel)
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.Run(async () =>
        {
            try
            {
                while (!progressCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, progressCts.Token);
                    item.DownloadedSize = progress.Sum();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    var dt = now - lastProgressTime;
                    if (dt > 0)
                    {
                        item.Speed = (item.DownloadedSize - lastBytes) / dt;
                        lastBytes = item.DownloadedSize;
                        lastProgressTime = now;
                    }
                    OnProgress?.Invoke(item);
                }
            }
            catch (OperationCanceledException) { }
        }, progressCts.Token);

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            // Stop the progress loop, then drain it before returning.
            progressCts.Cancel();
            try { await progressTask; } catch { }
            item.DownloadedSize = progress.Sum();
            OnProgress?.Invoke(item);
        }
    }

    private async Task DownloadPartAsync(DownloadItem item, string url,
        long start, long end, long[] progress, int idx, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var resp = await _client!.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // A server that ignores Range sends the whole file, which would overwrite the other
        // parts. Bail out before writing anything; the caller falls back to a single stream.
        if (resp.StatusCode != HttpStatusCode.PartialContent ||
            resp.Content.Headers.ContentRange?.From != start)
            throw new RangeNotHonoredException();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        // One file handle per part, reused for every chunk. ReadAsync from network
        // returns small slices, so opening a new FileStream per write would mean
        // thousands of open/seek/close cycles per part on a multi-GB download.
        await using var fs = new FileStream(item.Filepath!, FileMode.Open, FileAccess.Write,
            FileShare.Write, WriteBuffer, useAsync: true);
        fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[ChunkSize];
        long written = 0;

        while (true)
        {
            await WaitIfPausedAsync(item.Id, ct);
            ct.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress[idx] = written;
        }
    }

    private async Task WaitIfPausedAsync(int itemId, CancellationToken ct)
    {
        if (_pauseTcs.TryGetValue(itemId, out var tcs))
        {
            // Wait for resume or cancellation
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
    }

    public static string ExtractFilename(HttpResponseMessage resp, string url,
        bool fallback = true)
    {
        // Try Content-Disposition
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            var name = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                name = name.Trim('"', ' ');
                if (!string.IsNullOrEmpty(name))
                    return SanitizeFilename(name);
            }
        }

        // Try raw header with regex
        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var vals))
        {
            var raw = string.Join("; ", vals);

            var utf8Match = Regex.Match(raw, @"filename\*=UTF-8''([^;]+)", RegexOptions.IgnoreCase);
            if (utf8Match.Success)
                return SanitizeFilename(Uri.UnescapeDataString(utf8Match.Groups[1].Value));

            var fnMatch = Regex.Match(raw, @"filename=""?([^"";]+)""?", RegexOptions.IgnoreCase);
            if (fnMatch.Success)
                return SanitizeFilename(fnMatch.Groups[1].Value);
        }

        // Try URL path
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var name = Uri.UnescapeDataString(path.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "");
            if (!string.IsNullOrEmpty(name) && name.Contains('.'))
                return SanitizeFilename(name);
        }
        catch (Exception ex) { Debug.WriteLine($"ExtractFilename URL parse failed for {url}: {ex.Message}"); }

        if (!fallback) return "";

        // Hash-based fallback
        var hash = url.GetHashCode().ToString("x8");
        return $"download_{hash}";
    }

    private static string SanitizeFilename(string name)
    {
        // Keep alphanumeric, space, dot, dash, underscore, parens, brackets
        name = Regex.Replace(name, @"[^\w\s.\-_()[\]]", "");
        name = name.TrimStart('.');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    private static string? ClaimFilepath(string dir, string filename)
    {
        lock (FilenameLock)
        {
            var path = Path.Combine(dir, filename);
            if (ActivePaths.Contains(path)) return null;   // another download is writing it

            if (File.Exists(path))
            {
                // 0-byte placeholder from a prior failed download — reclaim it.
                try
                {
                    if (new FileInfo(path).Length == 0)
                    {
                        ActivePaths.Add(path);
                        return path;
                    }
                }
                catch { }
                return null;
            }

            // Create empty file to claim
            File.WriteAllBytes(path, []);
            ActivePaths.Add(path);
            return path;
        }
    }

    // Files that downloads (in any engine) are writing right now. A freshly claimed file
    // stays 0 bytes until its first write is flushed, so size alone can't tell a leftover
    // placeholder from a file another download just claimed.
    private static readonly HashSet<string> ActivePaths = new(StringComparer.OrdinalIgnoreCase);

    private static void MarkActive(string path)
    {
        lock (FilenameLock) ActivePaths.Add(path);
    }

    private static void ReleaseActive(string? path)
    {
        if (path != null) lock (FilenameLock) ActivePaths.Remove(path);
    }

    private sealed class RangeNotHonoredException()
        : Exception("Server ignored the byte range for a download part");

    public void Dispose()
    {
        _client?.Dispose();
        foreach (var cts in _cancelTokens.Values)
            cts.Dispose();
        _cancelTokens.Clear();
    }
}
