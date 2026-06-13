using System.Net;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class CookieHttpFetcher : IBepisDbFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;

    public CookieHttpFetcher(PluginSettings settings, RateLimiter rateLimiter, Action<string> log)
    {
        _rateLimiter = rateLimiter;
        _log = log;

        var cookies = new CookieContainer();
        if (settings.CfClearanceCookie is { Length: > 0 } cookie)
        {
            cookies.Add(new Cookie("cf_clearance", cookie, "/", "db.bepis.moe"));
        }

        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var userAgent = settings.UserAgent
            ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("Referer", "https://db.bepis.moe/");
    }

    public async Task<string?> FetchPageHtmlAsync(string url, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _log("BepisDB returned 403 Forbidden. Your cf_clearance cookie may have expired. " +
                     "Update settings.json with a fresh cookie from your browser, or switch to webview2 strategy.");
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log($"HTTP request failed for {url}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
