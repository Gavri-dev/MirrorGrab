using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FfResolver;

// Headless WebView2 resolver for fuckingfast.co. Clears the Cloudflare Turnstile the
// file page now requires, then hands back the signed dl.fuckingfast.co/dl/... link.
//
//   FfResolver <url> [timeoutSec]   -> resolve one link; print "OK <url>" / "ERR <why>"
//   FfResolver --server             -> read page URLs from stdin (one per line); print
//                                      one result line each; reuses a single browser.
//
// fuckingfast's DOWNLOAD button both opens an ad pop-up and (once Turnstile has issued a
// token) fires an htmx POST that redirects to the signed /dl/ link. So we suppress ad
// pop-ups and off-site ad redirects, and click the button on a timer until the real link
// appears. If the invisible Turnstile stalls, the window surfaces for a one-time tick.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        var host = new ResolverForm();

        if (args.Contains("--server"))
        {
            host.Ready += () =>
            {
                var th = new Thread(() => ServerLoop(host)) { IsBackground = true };
                th.Start();
            };
            Application.Run(host);
            return 0;
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: FfResolver <fuckingfast-url> [timeoutSec] | --server");
            return 2;
        }

        string url = args[0];
        int timeout = args.Length > 1 && int.TryParse(args[1], out var t) ? t : 60;
        string outp = "ERR no result";
        host.Ready += async () =>
        {
            var r = await host.ResolveAsync(url, timeout);
            outp = r.ok ? "OK " + r.value : "ERR " + r.value;
            host.BeginInvoke(new Action(host.Close));
        };
        Application.Run(host);
        Console.WriteLine(outp);
        return outp.StartsWith("OK") ? 0 : 1;
    }

    static void ServerLoop(ResolverForm host)
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == "QUIT") break;
            try
            {
                var task = host.ResolveAsync(line, 60);
                task.Wait();
                var r = task.Result;
                Console.Out.WriteLine(r.ok ? "OK " + r.value : "ERR " + r.value);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("ERR " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
                Console.Out.Flush();
            }
        }
        try { host.BeginInvoke(new Action(host.Close)); } catch { }
    }
}

sealed class ResolverForm : Form
{
    private const int InvisibleBudgetSec = 25;  // wait this long for auto-solve before surfacing

    private readonly WebView2 _web = new();
    private TaskCompletionSource<(bool ok, string value)>? _tcs;
    private volatile bool _captured;
    private volatile bool _navigating;   // true only for our own initial page load

    // A /dl/ link only counts once the requested page has replaced the previous one.
    // Until then, a late htmx redirect from the previous page (an extra click that was
    // still in flight) would otherwise be returned as this page's link — wrong file.
    private volatile bool _armed;
    private readonly HashSet<ulong> _pageNavIds = new();
    public event Action? Ready;

    public ResolverForm()
    {
        // Start off-screen: invisible to the user, but a real 1280x800 browser to the
        // page (tiny windows get flagged by bot heuristics).
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-3200, -3200);
        Size = new Size(1280, 800);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        Load += async (_, _) => await Init();
    }

    private async Task Init()
    {
        try
        {
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MirrorGrab", "wv2");
            Directory.CreateDirectory(udf);

            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Top-frame navigation: grab the /dl/ link; block ad redirects to other domains
            // (but always allow our own initial load and anything on fuckingfast.co).
            core.NavigationStarting += (_, e) =>
            {
                if (IsDlLink(e.Uri))
                {
                    e.Cancel = true;
                    if (_armed) Complete(true, e.Uri);
                }
                else if (_navigating)
                {
                    _pageNavIds.Add(e.NavigationId);
                }
                else if (!string.IsNullOrEmpty(e.Uri)
                         && !e.Uri.Contains("fuckingfast.co", StringComparison.OrdinalIgnoreCase)
                         && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true; // ad redirect — stay on the file page
                }
            };

            // The requested page's document has committed: the previous page is gone.
            core.ContentLoading += (_, e) =>
            {
                if (_pageNavIds.Contains(e.NavigationId)) _armed = true;
            };

            // The download can also arrive as a file download or a pop-up window.
            core.DownloadStarting += (_, e) => TryCapture(e.DownloadOperation.Uri, () => e.Cancel = true);
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;           // never actually open a pop-up (kills the ads)
                TryCapture(e.Uri, null);    // ...unless it's the real /dl/ link
            };

            // Faster loads (and starves ad images): drop images, media and fonts. Scripts,
            // stylesheets, documents and XHR still load, so Turnstile keeps working.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var c = e.ResourceContext;
                if (c is CoreWebView2WebResourceContext.Image
                      or CoreWebView2WebResourceContext.Media
                      or CoreWebView2WebResourceContext.Font)
                {
                    e.Response = env.CreateWebResourceResponse(null, 204, "No Content", "");
                }
            };

            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("init failed: " + ex.Message);
            Environment.Exit(3);
        }
    }

    private static bool IsDlLink(string? uri)
        => !string.IsNullOrEmpty(uri) &&
           uri.Contains("fuckingfast.co/dl/", StringComparison.OrdinalIgnoreCase);

    // Callable from any thread; marshals onto the UI thread. Requests are serialized by
    // the caller (server loop waits for each result before sending the next).
    public Task<(bool ok, string value)> ResolveAsync(string url, int timeoutSec)
    {
        if (InvokeRequired)
            return (Task<(bool ok, string value)>)Invoke(
                new Func<Task<(bool, string)>>(() => ResolveAsync(url, timeoutSec)));

        _tcs = new TaskCompletionSource<(bool, string)>();
        _captured = false;
        _ = RunResolve(url, timeoutSec);
        return _tcs.Task;
    }

    private async Task RunResolve(string url, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        var surfaceAt = DateTime.UtcNow.AddSeconds(Math.Min(InvisibleBudgetSec, timeoutSec));
        bool surfaced = false;
        try
        {
            _armed = false;
            _pageNavIds.Clear();
            _navigating = true;
            var navDone = new TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (_pageNavIds.Contains(e.NavigationId)) navDone.TrySetResult(true);
            }
            _web.CoreWebView2.NavigationCompleted += OnNav;
            _web.CoreWebView2.Navigate(url);
            await Task.WhenAny(navDone.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            _web.CoreWebView2.NavigationCompleted -= OnNav;
            _navigating = false;

            // Click the download button on a timer. The first clicks may only spawn ad
            // pop-ups (suppressed); once Turnstile has issued a token, a click fires the
            // htmx POST that redirects to /dl/, which our handlers capture. We never read
            // the token variable (its name isn't stable) — we just keep clicking.
            const string clickJs =
                @"(function(){var el=document.querySelector('[hx-post*=""/go""]')||" +
                @"document.querySelector('button,a');if(el){el.click();return '1';}return '0';})()";

            var lastClick = DateTime.MinValue;
            while (DateTime.UtcNow < deadline && !_captured)
            {
                if ((DateTime.UtcNow - lastClick).TotalMilliseconds > 900)
                {
                    try { await _web.CoreWebView2.ExecuteScriptAsync(clickJs); } catch { }
                    lastClick = DateTime.UtcNow;
                }
                if (!surfaced && DateTime.UtcNow > surfaceAt) { surfaced = true; Surface(); }
                await Task.Delay(120);
            }

            if (!_captured)
                Complete(false, surfaced ? "check not completed in time" : "no /dl/ link (timed out)");
        }
        catch (Exception ex) { Complete(false, ex.Message); }
        finally { if (surfaced) Hide(); }
    }

    // Bring the hidden browser on-screen so the user can complete an interactive check.
    private void Surface()
    {
        Text = "MirrorGrab — please complete the check to continue";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(480, 640);
        Location = new Point(wa.X + (wa.Width - 480) / 2, wa.Y + (wa.Height - 640) / 2);
        TopMost = true;
        Show();
        BringToFront();
        Activate();
    }

    private new void Hide()
    {
        TopMost = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Location = new Point(-3200, -3200);
        Size = new Size(1280, 800);
    }

    private void TryCapture(string uri, Action? cancel)
    {
        if (!IsDlLink(uri)) return;
        try { cancel?.Invoke(); } catch { }
        if (!_captured && _armed) Complete(true, uri);
    }

    private void Complete(bool ok, string value)
    {
        var tcs = _tcs;
        if (tcs == null) return;
        _captured = true;
        _tcs = null;
        tcs.TrySetResult((ok, value));
    }
}
