using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class WebView2Fetcher : IBepisDbFetcher
{
    private const string ApiBase = "https://db.bepis.moe/api/frontend/cardPage";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly string _userDataFolder;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public WebView2Fetcher(string storageDirectory, RateLimiter rateLimiter, Action<string> log)
    {
        _rateLimiter = rateLimiter;
        _log = log;
        _userDataFolder = Path.Combine(storageDirectory, "webview2-data");
    }

    public static bool IsAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder).ConfigureAwait(false);

            _controller = await _environment.CreateCoreWebView2ControllerAsync(IntPtr.Zero).ConfigureAwait(false);
            _webView = _controller.CoreWebView2;

            _controller.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
            _controller.IsVisible = false;

            _initialized = true;
            _log("WebView2 initialized successfully.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);
        var url = $"{ApiBase}?cardType={cardType}&id={numericId}";

        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            if (_webView is null)
            {
                _log("WebView2 not initialized.");
                return null;
            }

            var tcs = new TaskCompletionSource<string?>();
            using var ctr = ct.Register(() => tcs.TrySetCanceled(ct));
            using var timeout = new CancellationTokenSource(NavigationTimeout);
            using var timeoutReg = timeout.Token.Register(
                () => tcs.TrySetResult(null));

            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (!args.IsSuccess)
                {
                    _log($"WebView2 navigation failed: {args.WebErrorStatus}");
                    tcs.TrySetResult(null);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var bodyJson = await _webView.ExecuteScriptAsync(
                            "document.body.innerText").ConfigureAwait(false);

                        // ExecuteScriptAsync returns a JSON-encoded string
                        if (bodyJson is not null)
                            bodyJson = JsonSerializer.Deserialize<string>(bodyJson);

                        tcs.TrySetResult(bodyJson);
                    }
                    catch (Exception ex)
                    {
                        _log($"WebView2 script execution failed: {ex.Message}");
                        tcs.TrySetResult(null);
                    }
                }, ct);
            }

            _webView.NavigationCompleted += OnNavigationCompleted;
            try
            {
                _webView.Navigate(url);
                var json = await tcs.Task.ConfigureAwait(false);

                if (json is null) return null;

                var apiResponse = JsonSerializer.Deserialize<BepisDbApiResponse>(json);
                if (apiResponse?.Type != "success" || apiResponse.Data?.Card is null)
                {
                    _log($"BepisDB API returned unexpected response for {cardType}_{numericId}: {apiResponse?.Error ?? "no card data"}");
                    return null;
                }

                return apiResponse.Data.Card;
            }
            finally
            {
                _webView.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"WebView2 fetch failed for {cardType}_{numericId}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _controller?.Close();
        _controller = null;
        _webView = null;
        _environment = null;
        _initLock.Dispose();
    }
}
