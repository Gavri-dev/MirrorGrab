using System.Net.Http.Headers;

namespace MirrorGrab.Services;

/// <summary>
/// Applies a realistic desktop-Chrome header set to an <see cref="HttpClient"/>.
/// A bare "Mozilla/5.0" now gets a flat HTTP 403 from Cloudflare-fronted hosts
/// like fuckingfast.co — this is what lets our requests through.
/// Keep every outbound client going through here so the header set never rots
/// unevenly across the app again.
/// </summary>
public static class BrowserHeaders
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static void Apply(HttpClient client)
    {
        var h = client.DefaultRequestHeaders;

        h.UserAgent.Clear();
        h.UserAgent.ParseAdd(UserAgent);

        h.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        h.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        // Client hints + fetch-metadata Cloudflare inspects on real Chrome traffic.
        TryAdd(h, "Upgrade-Insecure-Requests", "1");
        TryAdd(h, "sec-ch-ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        TryAdd(h, "sec-ch-ua-mobile", "?0");
        TryAdd(h, "sec-ch-ua-platform", "\"Windows\"");
        TryAdd(h, "Sec-Fetch-Dest", "document");
        TryAdd(h, "Sec-Fetch-Mode", "navigate");
        TryAdd(h, "Sec-Fetch-Site", "none");
        TryAdd(h, "Sec-Fetch-User", "?1");
    }

    private static void TryAdd(HttpRequestHeaders h, string name, string value)
        => h.TryAddWithoutValidation(name, value);
}
