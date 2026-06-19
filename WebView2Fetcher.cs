using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SceneGallery.Plugin.BepisDb;

internal sealed partial class WebView2Fetcher : IBepisDbFetcher
{
    private const string ApiBase = "https://db.bepis.moe/api/frontend/cardPage";
    private const string ChallengeUrl = "https://db.bepis.moe/";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    private readonly RateLimiter _rateLimiter;
    private readonly Action<string> _log;
    private readonly string _userDataFolder;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _challengePassed;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private Windows.System.DispatcherQueueController? _dqController;
    private Windows.System.DispatcherQueue? _dq;

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

    private Task RunOnWebViewThread(Func<Task> asyncFunc)
    {
        var tcs = new TaskCompletionSource();
        if (!_dq!.TryEnqueue(async () =>
        {
            try
            {
                await asyncFunc();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new ObjectDisposedException(nameof(WebView2Fetcher), "DispatcherQueue is shut down."));
        }
        return tcs.Task;
    }

    private Task<T> RunOnWebViewThread<T>(Func<Task<T>> asyncFunc)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!_dq!.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await asyncFunc());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new ObjectDisposedException(nameof(WebView2Fetcher), "DispatcherQueue is shut down."));
        }
        return tcs.Task;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _dqController = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
            _dq = _dqController.DispatcherQueue;

            await RunOnWebViewThread(async () =>
            {
                _hwnd = CreateHostWindow(visible: false);
                if (_hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create host window for WebView2.");

                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: _userDataFolder);

                _controller = await _environment.CreateCoreWebView2ControllerAsync(_hwnd);
                _webView = _controller.CoreWebView2;

                _controller.Bounds = new System.Drawing.Rectangle(0, 0, 800, 600);
                _controller.IsVisible = false;
            });

            _initialized = true;
            _log("WebView2 initialized successfully.");
        }
        catch (Exception ex)
        {
            _log($"WebView2 initialization failed: {ex.Message}");
            DestroyHostWindow();
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct)
    {
        var url = $"{ApiBase}?cardType={cardType}&id={numericId}";

        for (var attempt = 0; ; attempt++)
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

                var json = await RunOnWebViewThread(() => NavigateAndExtractAsync(url, ct)).ConfigureAwait(false);

                if (json is null && !_challengePassed)
                {
                    _log("Cloudflare challenge detected. Opening browser for captcha...");
                    var passed = await RunOnWebViewThread(() => ShowChallengeWindowAsync(ct)).ConfigureAwait(false);
                    if (passed)
                    {
                        _challengePassed = true;
                        _log("Cloudflare challenge passed. Retrying API call...");
                        json = await RunOnWebViewThread(() => NavigateAndExtractAsync(url, ct)).ConfigureAwait(false);
                    }
                    else
                    {
                        _log("Cloudflare challenge was not completed.");
                        return null;
                    }
                }

                if (json is null)
                {
                    if (attempt < RetryDelays.Length)
                    {
                        _log($"WebView2 fetch returned no data for {cardType}_{numericId}, retrying in {RetryDelays[attempt].TotalSeconds}s");
                        await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                        continue;
                    }
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
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                _log($"WebView2 fetch failed for {cardType}_{numericId}: {ex.Message}, retrying in {RetryDelays[attempt].TotalSeconds}s");
                await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"WebView2 fetch failed for {cardType}_{numericId}: {ex.Message}");
                return null;
            }
        }
    }

    private async Task<string?> NavigateAndExtractAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>();
        using var ctr = ct.Register(() => tcs.TrySetCanceled(ct));
        using var timeout = new CancellationTokenSource(NavigationTimeout);
        using var timeoutReg = timeout.Token.Register(() => tcs.TrySetResult(null));

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                _log($"WebView2 navigation failed: {args.WebErrorStatus}");
                tcs.TrySetResult(null);
                return;
            }

            ExtractBodyAsync(tcs);
        }

        _webView!.NavigationCompleted += OnNavigationCompleted;
        try
        {
            _webView.Navigate(url);
            return await tcs.Task;
        }
        finally
        {
            _webView.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private async void ExtractBodyAsync(TaskCompletionSource<string?> tcs)
    {
        try
        {
            var bodyJson = await _webView!.ExecuteScriptAsync("document.body.innerText");

            if (bodyJson is not null)
                bodyJson = JsonSerializer.Deserialize<string>(bodyJson);

            if (bodyJson is not null && bodyJson.TrimStart().StartsWith('{'))
                tcs.TrySetResult(bodyJson);
            else
                tcs.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _log($"WebView2 script execution failed: {ex.Message}");
            tcs.TrySetResult(null);
        }
    }

    private async Task<bool> ShowChallengeWindowAsync(CancellationToken ct)
    {
        ShowWindow(_hwnd, SW_SHOW);
        SetWindowText(_hwnd, "BepisDB - Complete Cloudflare Challenge");
        SetWindowPos(_hwnd, IntPtr.Zero, 100, 100, 820, 660, SWP_NOZORDER);
        _controller!.IsVisible = true;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, 800, 600);

        var tcs = new TaskCompletionSource<bool>();
        using var ctr = ct.Register(() => tcs.TrySetCanceled(ct));
        using var timeout = new CancellationTokenSource(ChallengeTimeout);
        using var timeoutReg = timeout.Token.Register(() => tcs.TrySetResult(false));

        _webView!.Navigate(ChallengeUrl);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) return;
            CheckChallengePassedAsync(tcs);
        }

        _webView.NavigationCompleted += OnNavigationCompleted;
        try
        {
            var result = await tcs.Task;

            _controller.IsVisible = false;
            ShowWindow(_hwnd, SW_HIDE);

            return result;
        }
        finally
        {
            _webView.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private async void CheckChallengePassedAsync(TaskCompletionSource<bool> tcs)
    {
        try
        {
            var title = await _webView!.ExecuteScriptAsync("document.title");
            if (title is not null)
                title = JsonSerializer.Deserialize<string>(title);

            if (title is not null && title.Contains("BepisDB", StringComparison.OrdinalIgnoreCase)
                && !title.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            _log($"WebView2 challenge check failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_dq is not null)
        {
            var tcs = new TaskCompletionSource();
            if (_dq.TryEnqueue(() =>
            {
                _controller?.Close();
                _controller = null;
                _webView = null;
                _environment = null;
                DestroyHostWindow();
                tcs.SetResult();
            }))
            {
                tcs.Task.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                _controller?.Close();
                _controller = null;
                _webView = null;
                _environment = null;
            }
        }
        else
        {
            _controller?.Close();
            _controller = null;
            _webView = null;
            _environment = null;
            DestroyHostWindow();
        }

        _initLock.Dispose();

        if (_dqController is not null)
        {
            _dqController.ShutdownQueueAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            _dqController = null;
        }
    }

    // -- Win32 window hosting for WebView2 --

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const uint SWP_NOZORDER = 0x0004;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowText(IntPtr hWnd, string lpString);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static IntPtr CreateHostWindow(bool visible)
    {
        var style = visible ? WS_OVERLAPPEDWINDOW : 0u;
        var hwnd = CreateWindowEx(0, "Static", "BepisDB WebView2", style,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (visible && hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_SHOW);
        return hwnd;
    }

    private void DestroyHostWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
