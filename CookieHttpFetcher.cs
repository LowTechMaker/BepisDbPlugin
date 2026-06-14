using System.Net;
using System.Text.Json;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class CookieHttpFetcher : IBepisDbFetcher
{
    private const string ApiBase = "https://db.bepis.moe/api/frontend/cardPage";

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly Action _markCookieSetupRequired;

    public CookieHttpFetcher(
        PluginSettings settings,
        RateLimiter rateLimiter,
        Action<string> log,
        Action markCookieSetupRequired)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _markCookieSetupRequired = markCookieSetupRequired;

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
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("Referer", "https://db.bepis.moe/");
    }

    public async Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);
        var url = $"{ApiBase}?cardType={cardType}&id={numericId}";

        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _markCookieSetupRequired();
                _log("BepisDB API returned 403 Forbidden. Your cf_clearance cookie may have expired. " +
                     "Use the cookie setup button on the import page to refresh it.");
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (IsCloudflareChallenge(json))
            {
                _markCookieSetupRequired();
                _log("BepisDB API returned a Cloudflare challenge. Refreshing cookies is required.");
                return null;
            }

            var apiResponse = JsonSerializer.Deserialize<BepisDbApiResponse>(json);

            if (apiResponse?.Type != "success" || apiResponse.Data?.Card is null)
            {
                _log($"BepisDB API returned unexpected response for {cardType}_{numericId}: {apiResponse?.Error ?? "no card data"}");
                return null;
            }

            return apiResponse.Data.Card;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log($"BepisDB API request failed for {cardType}_{numericId}: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            _log($"BepisDB API returned invalid JSON for {cardType}_{numericId}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> HasUsableCookiesAsync(CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        try
        {
            var response = await _http.GetAsync($"{ApiBase}?cardType=KKSCENE&id=1", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden || IsCloudflareChallenge(body))
            {
                _markCookieSetupRequired();
                _log("BepisDB cookie validation hit Cloudflare. Cookie setup is required before import.");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log($"BepisDB cookie validation request failed; continuing without forcing setup: {ex.Message}");
            return true;
        }
    }

    private static bool IsCloudflareChallenge(string body) =>
        body.Contains("cf_chl", StringComparison.OrdinalIgnoreCase)
        || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || body.Contains("Enable JavaScript and cookies", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
