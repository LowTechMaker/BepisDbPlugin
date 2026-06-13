using Microsoft.Web.WebView2.Core;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class WebView2Fetcher : IBepisDbFetcher
{
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

    public async Task<string?> FetchPageHtmlAsync(string url, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

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
                        // Wait briefly for any JS to finish rendering
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        var html = await _webView.ExecuteScriptAsync(
                            "document.documentElement.outerHTML").ConfigureAwait(false);

                        // ExecuteScriptAsync returns a JSON-encoded string
                        if (html is not null && html.StartsWith('"') && html.EndsWith('"'))
                        {
                            html = System.Text.Json.JsonSerializer.Deserialize<string>(html);
                        }

                        tcs.TrySetResult(html);
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
                return await tcs.Task.ConfigureAwait(false);
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
            _log($"WebView2 fetch failed for {url}: {ex.Message}");
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
