using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirrorGrab.Services;

public class LinkExtractor : IDisposable
{
    private readonly HttpClient _client;
    private FuckingFastResolver? _ff;   // lazily started; one sidecar per extraction run

    public LinkExtractor(HttpClient? client = null)
    {
        _client = client ?? CreateClient();
    }

    // ---- Repack page expansion (paste a FitGirl URL, get its mirror links) ----

    // A fuckingfast.co *page* link as it appears on a repack page: /<code>[#filename].
    private static readonly Regex FfPageLink =
        new(@"https?://fuckingfast\.co/[A-Za-z0-9]+(?:#[^\s""'<>]+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True if the URL is a repack listing page we know how to scrape.</summary>
    public static bool IsRepackPage(string url)
    {
        try { return new Uri(url).Host.Contains("fitgirl-repacks.site", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Fetches a repack page (e.g. FitGirl) and returns the fuckingfast.co mirror links it
    /// lists, de-duplicated in page order. These are page URLs still to be resolved.
    /// </summary>
    public async Task<List<string>> ExpandRepackPageAsync(string pageUrl)
    {
        var html = await _client.GetStringAsync(pageUrl);
        var seen = new HashSet<string>();
        var links = new List<string>();
        foreach (Match m in FfPageLink.Matches(html))
        {
            var full = m.Value;
            if (seen.Add(full.Split('#')[0]))   // key on the code, keep the #filename hint
                links.Add(full);
        }
        return links;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        BrowserHeaders.Apply(c);
        return c;
    }

    public async Task<string?> ExtractAsync(string url)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch { return null; }

        var host = uri.Host.ToLower();

        // Exceptions propagate on purpose: the caller shows ex.Message so a broken
        // host tells you WHY (Cloudflare / layout change) instead of a silent null.
        if (host.Contains("fuckingfast.co"))
            return await ExtractFuckingFast(url);
        if (host.Contains("datanodes.to"))
            return await ExtractDatanodes(url);

        throw new NotSupportedException($"Unsupported host: {host}. Only fuckingfast.co and datanodes.to are supported.");
    }

    // fuckingfast.co moved the direct link behind a Cloudflare Turnstile captcha and
    // an htmx POST, so a plain HTTP fetch can no longer scrape it. Resolution now runs
    // through the headless-WebView2 sidecar, which clears the captcha and returns the
    // signed dl.fuckingfast.co/dl/... URL (usable by the normal download engine).
    private async Task<string?> ExtractFuckingFast(string url)
    {
        _ff ??= new FuckingFastResolver();
        return await _ff.ResolveAsync(url);
    }

    private async Task<string?> ExtractDatanodes(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) return null;

        // segments: [segment0, fileCode, filename]
        var fileCode = segments.Length >= 3 ? segments[1] : segments[0];
        var fileName = segments.Length >= 3 ? segments[2] : segments[1];

        var baseUrl = $"{uri.Scheme}://{uri.Host}";

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("lang", "english"));
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("file_name", fileName));
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("file_code", fileCode));

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        BrowserHeaders.Apply(client);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("op", "download2"),
            new KeyValuePair<string, string>("id", fileCode),
            new KeyValuePair<string, string>("rand", ""),
            new KeyValuePair<string, string>("method_free", "Free Download >>"),
            new KeyValuePair<string, string>("dl", "1"),
        });

        var resp = await client.PostAsync($"{baseUrl}/download", form);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("url", out var urlEl))
            return Uri.UnescapeDataString(urlEl.GetString() ?? "");

        return null;
    }

    public void Dispose()
    {
        _client.Dispose();
        _ff?.Dispose();
    }
}
