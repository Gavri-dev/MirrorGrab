using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MirrorGrab.Services;

public class LinkExtractor : IDisposable
{
    private readonly HttpClient _client;

    public LinkExtractor(HttpClient? client = null)
    {
        _client = client ?? CreateClient();
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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        return c;
    }

    public async Task<string?> ExtractAsync(string url)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch { return null; }

        var host = uri.Host.ToLower();

        if (host.Contains("fuckingfast.co"))
        {
            try { return await ExtractFuckingFast(url); }
            catch { return null; }
        }
        if (host.Contains("datanodes.to"))
        {
            try { return await ExtractDatanodes(url); }
            catch { return null; }
        }

        throw new NotSupportedException($"Unsupported host: {host}. Only fuckingfast.co and datanodes.to are supported.");
    }

    private async Task<string?> ExtractFuckingFast(string url)
    {
        var html = await _client.GetStringAsync(url);
        var match = Regex.Match(html, @"https://fuckingfast\.co/dl/[a-zA-Z0-9_-]+");
        return match.Success ? match.Value : null;
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
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("lang", "english"));
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("file_name", fileName));
        handler.CookieContainer.Add(new Uri(baseUrl), new Cookie("file_code", fileCode));

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

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

    public void Dispose() => _client.Dispose();
}
