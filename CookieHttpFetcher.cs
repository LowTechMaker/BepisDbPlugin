using System.Net;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class CookieHttpFetcher : IBepisDbFetcher
{
    private const string ApiBase = "https://db.bepis.moe/api/frontend/cardPage";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly Action _markCookieSetupRequired;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public CookieHttpFetcher(
        PluginSettings settings,
        RateLimiter rateLimiter,
        Action<string> log,
        Action markCookieSetupRequired)
        : this(settings, rateLimiter, log, markCookieSetupRequired, CreateHandler(settings))
    {
    }

    internal CookieHttpFetcher(
        PluginSettings settings,
        RateLimiter rateLimiter,
        Action<string> log,
        Action markCookieSetupRequired,
        HttpMessageHandler handler,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _markCookieSetupRequired = markCookieSetupRequired;
        _retryDelays = retryDelays ?? RetryDelays;
        _http = new HttpClient(handler, disposeHandler: true)
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

    private static HttpMessageHandler CreateHandler(PluginSettings settings)
    {
        var cookies = new CookieContainer();
        if (settings.CfClearanceCookie is { Length: > 0 } cookie)
            cookies.Add(new Cookie("cf_clearance", cookie, "/", "db.bepis.moe"));

        return new SocketsHttpHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
    }

    public async Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct)
    {
        var url = $"{ApiBase}?cardType={cardType}&id={numericId}";

        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests
                                || (int)response.StatusCode >= 500;
                if (transient)
                {
                    if (attempt < _retryDelays.Count)
                    {
                        _log($"BepisDB API returned {(int)response.StatusCode} for {cardType}_{numericId}, retrying in {_retryDelays[attempt].TotalSeconds}s");
                        response.Dispose();
                        await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
                        continue;
                    }

                    _log($"BepisDB API returned {(int)response.StatusCode} for {cardType}_{numericId} after retries were exhausted.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Cloudflare is an authentication/setup state rather than a
                // resource, schema, or transient API result, so it stays on its
                // dedicated cookie-setup path.
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    HandleForbidden($"API request for {cardType}_{numericId}", json);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    LogSchemaError(cardType, numericId, $"HTTP {(int)response.StatusCode}", json);
                    return null;
                }

                if (BepisDbResponseParser.IsCloudflareChallenge(json))
                {
                    _markCookieSetupRequired();
                    _log("BepisDB API returned a Cloudflare challenge. Refreshing cookies is required.");
                    return null;
                }

                var parsed = BepisDbResponseParser.ParseCard(json);
                if (parsed.SchemaError is { } error)
                {
                    LogSchemaError(cardType, numericId, error, json);
                    return null;
                }
                return parsed.Card;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (attempt < _retryDelays.Count)
            {
                _log($"BepisDB API request timed out for {cardType}_{numericId}: {ex.Message}, retrying in {_retryDelays[attempt].TotalSeconds}s");
                await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _log($"BepisDB API request timed out for {cardType}_{numericId}: {ex.Message}");
                return null;
            }
            catch (HttpRequestException ex) when (attempt < _retryDelays.Count)
            {
                _log($"BepisDB API request failed for {cardType}_{numericId}: {ex.Message}, retrying in {_retryDelays[attempt].TotalSeconds}s");
                await Task.Delay(_retryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _log($"BepisDB API request failed for {cardType}_{numericId}: {ex.Message}");
                return null;
            }
        }
    }

    public async Task<bool> HasUsableCookiesAsync(CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        try
        {
            using var response = await _http.GetAsync($"{ApiBase}?cardType=KKSCENE&id=1", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                HandleForbidden("cookie validation", body);
                return false;
            }

            if (BepisDbResponseParser.IsCloudflareChallenge(body))
            {
                _markCookieSetupRequired();
                _log("BepisDB cookie validation hit Cloudflare. Cookie setup is required before import.");
                return false;
            }

            if (response.IsSuccessStatusCode)
                return true;

            _log($"BepisDB cookie validation could not confirm cookie usability: HTTP {(int)response.StatusCode}.");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _log($"BepisDB cookie validation timed out; cookie is treated as unavailable: {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _log($"BepisDB cookie validation request failed; cookie is treated as unavailable: {ex.Message}");
            return false;
        }
    }

    private void HandleForbidden(string context, string body)
    {
        _markCookieSetupRequired();
        if (BepisDbResponseParser.IsCloudflareChallenge(body))
        {
            _log($"BepisDB {context} returned 403 with a confirmed Cloudflare challenge. Cookie setup is required.");
        }
        else
        {
            _log($"BepisDB {context} returned 403 without Cloudflare challenge markers. Cookie setup is required; the response may indicate permissions or blocking.");
        }
    }

    private void LogSchemaError(string cardType, string numericId, string reason, string responseBody)
    {
        const int maxSummaryLength = 500;
        var summary = responseBody.Length <= maxSummaryLength
            ? responseBody
            : responseBody[..maxSummaryLength];
        _log($"warning: BepisDB schema error for {cardType}_{numericId} ({reason}); response: {summary}");
    }

    public void Dispose() => _http.Dispose();
}
