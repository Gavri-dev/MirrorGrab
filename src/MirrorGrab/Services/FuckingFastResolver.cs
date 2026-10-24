using System.Diagnostics;

namespace MirrorGrab.Services;

/// <summary>
/// Drives the out-of-process FfResolver.exe (headless WebView2) to turn a
/// fuckingfast.co page URL into its signed dl.fuckingfast.co/dl/... direct link,
/// clearing the Cloudflare Turnstile the page now requires.
///
/// One resolver process is reused for a whole extraction run: it keeps a single
/// hidden browser alive so the Turnstile clearance cookie persists, and it stays
/// out-of-process so its WinForms message pump can't fight Avalonia's UI thread
/// (and a resolver crash can't take the app down). Requests are serialized.
/// </summary>
public sealed class FuckingFastResolver : IDisposable
{
    private readonly string _exePath;
    private Process? _proc;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FuckingFastResolver(string? exePath = null)
        => _exePath = exePath ?? LocateExeOrNull() ?? throw new FileNotFoundException(
            "FfResolver.exe not found. Build the FfResolver project so it is copied next to MirrorGrab.");

    /// <summary>True if the sidecar resolver executable can be located.</summary>
    public static bool IsAvailable => LocateExeOrNull() != null;

    public async Task<string?> ResolveAsync(string pageUrl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureStarted();

            await _proc!.StandardInput.WriteLineAsync(pageUrl.AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);

            string? line;
            try
            {
                line = await _proc.StandardOutput.ReadLineAsync(ct)
                                  .AsTask()
                                  .WaitAsync(TimeSpan.FromSeconds(120), ct);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The read is still pending on this process, and its late answer would be
                // taken as the next link's. Stop it; the next call starts a fresh resolver.
                StopProcess();
                throw;
            }

            if (line is null)
                throw new InvalidOperationException("Resolver process closed unexpectedly.");
            if (line.StartsWith("OK ", StringComparison.Ordinal))
                return line[3..].Trim();
            if (line.StartsWith("ERR ", StringComparison.Ordinal))
                throw new InvalidOperationException("Resolver: " + line[4..].Trim());

            throw new InvalidOperationException("Unexpected resolver output: " + line);
        }
        finally { _gate.Release(); }
    }

    private void StopProcess()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); }
        catch { /* already gone */ }
        _proc?.Dispose();
        _proc = null;
    }

    private void EnsureStarted()
    {
        if (_proc is { HasExited: false }) return;
        _proc?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = "--server",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FfResolver.exe");

        // Drain stderr so a full pipe buffer can never stall the child.
        _ = Task.Run(async () =>
        {
            try { while (await _proc.StandardError.ReadLineAsync() is not null) { } }
            catch { /* process gone */ }
        });
    }

    private static string? LocateExeOrNull()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "resolver", "FfResolver.exe"),
            Path.Combine(baseDir, "FfResolver.exe"),
        };
        // dev fallback: sibling project build output
        foreach (var cfg in new[] { "Release", "Debug" })
            candidates.Add(Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "FfResolver", "bin", cfg, "net8.0-windows", "FfResolver.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.StandardInput.WriteLine("QUIT"); _proc.StandardInput.Flush(); } catch { }
                if (!_proc.WaitForExit(3000)) _proc.Kill(true);
            }
        }
        catch { /* best effort */ }
        _proc?.Dispose();
        _gate.Dispose();
    }
}
