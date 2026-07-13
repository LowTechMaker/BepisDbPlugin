using System.Net;
using System.Text;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class CookieHttpFetcherTests
{
    private const string ValidCardJson = """
        {
          "type": "success",
          "data": {
            "card": {
              "cardType": "KKSCENE",
              "id": 123,
              "customName": "Shared Title",
              "uploader": { "id": 42, "username": "Shared Artist" },
              "tags": [],
              "downloadCount": 7
            }
          }
        }
        """;

    [Fact]
    public async Task SharedFetch_CallerCancellationDoesNotCancelOtherCallerOrCacheWrite()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new PluginTestContext(
            WaitThenRespondJson(requestStarted, releaseResponse, ValidCardJson));
        using var callerCancellation = new CancellationTokenSource();

        var callerA = context.FetchArtworkAsync(callerCancellation.Token);
        await requestStarted.Task;
        var callerB = context.FetchArtworkAsync();

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await callerA);
        releaseResponse.SetResult(true);

        var resultB = await callerB;
        var cached = await context.FetchArtworkAsync();

        Assert.Equal("Shared Title", resultB?.Title);
        Assert.Equal("Shared Title", cached?.Title);
        Assert.True(cached?.IsSavedLocally);
        Assert.Equal(1, context.Handler.CallCount);
    }

    [Fact]
    public async Task FetchCard_404_ReturnsNullWithoutRetry()
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.NotFound));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, context.Handler.CallCount);
    }

    [Theory]
    [InlineData("<html><body>cf_chl challenge</body></html>", "confirmed Cloudflare challenge")]
    [InlineData("Forbidden by policy", "without Cloudflare challenge markers")]
    public async Task FetchCard_403_UsesCookieSetupAndDistinguishesChallengeLog(
        string body,
        string expectedLog)
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.Forbidden, body));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Null(result);
        Assert.True(context.CookieSetupRequired);
        Assert.Equal(1, context.Handler.CallCount);
        Assert.Contains(context.Logs, message => message.Contains(expectedLog, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchCard_Other4xx_IsSchemaErrorWithoutRetryAndTruncatesBody()
    {
        var body = new string('x', 500) + "TRUNCATED-TAIL";
        using var context = new FetcherTestContext(Respond(HttpStatusCode.BadRequest, body));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, context.Handler.CallCount);
        var warning = Assert.Single(context.Logs, message => message.Contains("schema error", StringComparison.Ordinal));
        Assert.Contains("HTTP 400", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATED-TAIL", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchCard_429_RetriesThenSucceeds()
    {
        using var context = new FetcherTestContext(
            Respond(HttpStatusCode.TooManyRequests),
            RespondJson(ValidCardJson));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Equal(123, result?.Id);
        Assert.Equal(2, context.Handler.CallCount);
    }

    [Fact]
    public async Task FetchCard_500_RetriesThenSucceeds()
    {
        using var context = new FetcherTestContext(
            Respond(HttpStatusCode.InternalServerError),
            RespondJson(ValidCardJson));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Equal(123, result?.Id);
        Assert.Equal(2, context.Handler.CallCount);
    }

    [Fact]
    public async Task FetchCard_NetworkFailure_RetriesThenSucceeds()
    {
        using var context = new FetcherTestContext(
            Throw(new HttpRequestException("offline")),
            RespondJson(ValidCardJson));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Equal(123, result?.Id);
        Assert.Equal(2, context.Handler.CallCount);
    }

    [Theory]
    [InlineData("not-json", "invalid JSON")]
    [InlineData("{\"type\":\"success\",\"data\":{}}", "no card data")]
    [InlineData("{\"type\":\"success\",\"data\":{\"card\":{}}}", "missing id or cardType")]
    public async Task FetchCard_SchemaErrors_ReturnNullWithoutRetry(string body, string expectedReason)
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.OK, body));

        var result = await context.Fetcher.FetchCardAsync("KKSCENE", "123", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, context.Handler.CallCount);
        Assert.Contains(context.Logs, message =>
            message.Contains("warning:", StringComparison.Ordinal)
            && message.Contains(expectedReason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HasUsableCookies_2xxWithoutChallenge_ReturnsTrue()
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.OK, "{}"));

        var result = await context.Fetcher.HasUsableCookiesAsync(CancellationToken.None);

        Assert.True(result);
        Assert.False(context.CookieSetupRequired);
    }

    [Fact]
    public async Task HasUsableCookies_2xxChallenge_ReturnsFalseAndRequiresSetup()
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.OK, "Just a moment"));

        var result = await context.Fetcher.HasUsableCookiesAsync(CancellationToken.None);

        Assert.False(result);
        Assert.True(context.CookieSetupRequired);
    }

    [Theory]
    [InlineData("cf_chl", "confirmed Cloudflare challenge")]
    [InlineData("Forbidden by policy", "without Cloudflare challenge markers")]
    public async Task HasUsableCookies_403_ReturnsFalseAndDistinguishesChallengeLog(
        string body,
        string expectedLog)
    {
        using var context = new FetcherTestContext(Respond(HttpStatusCode.Forbidden, body));

        var result = await context.Fetcher.HasUsableCookiesAsync(CancellationToken.None);

        Assert.False(result);
        Assert.True(context.CookieSetupRequired);
        Assert.Contains(context.Logs, message => message.Contains(expectedLog, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HasUsableCookies_NonSuccessStatus_ReturnsFalse(HttpStatusCode statusCode)
    {
        using var context = new FetcherTestContext(Respond(statusCode));

        var result = await context.Fetcher.HasUsableCookiesAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, context.Handler.CallCount);
    }

    [Fact]
    public async Task HasUsableCookies_NetworkFailure_ReturnsFalse()
    {
        using var context = new FetcherTestContext(Throw(new HttpRequestException("offline")));

        var result = await context.Fetcher.HasUsableCookiesAsync(CancellationToken.None);

        Assert.False(result);
        Assert.Contains(context.Logs, message => message.Contains("treated as unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAuthorInfo_PreCanceledToken_Throws()
    {
        using var context = new PluginTestContext(RespondJson(ValidCardJson));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Plugin.GetAuthorInfoAsync(
            new AuthorKey(BepisDbFilenameParser.ProviderId, "42"),
            forceRefresh: true,
            cancellation.Token));
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(
        HttpStatusCode statusCode,
        string body = "")
        => (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondJson(
        string json)
        => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(
        Exception exception)
        => (_, _) => Task.FromException<HttpResponseMessage>(exception);

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> WaitThenRespondJson(
        TaskCompletionSource<bool> requestStarted,
        TaskCompletionSource<bool> releaseResponse,
        string json)
        => async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SceneGallery.Plugin.BepisDb.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for a test-only temporary directory.
        }
    }

    private sealed class FetcherTestContext : IDisposable
    {
        public FetcherTestContext(
            params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            Handler = new SequenceHttpMessageHandler(responses);
            Fetcher = new CookieHttpFetcher(
                new PluginSettings { CfClearanceCookie = "test-cookie" },
                new RateLimiter(TimeSpan.Zero),
                Logs.Add,
                () => CookieSetupRequired = true,
                Handler,
                [TimeSpan.Zero, TimeSpan.Zero]);
        }

        public SequenceHttpMessageHandler Handler { get; }
        public CookieHttpFetcher Fetcher { get; }
        public List<string> Logs { get; } = [];
        public bool CookieSetupRequired { get; private set; }

        public void Dispose() => Fetcher.Dispose();
    }

    private sealed class PluginTestContext : IDisposable
    {
        private readonly string _storageDirectory = CreateTempDirectory();

        public PluginTestContext(
            params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            Handler = new SequenceHttpMessageHandler(responses);
            var fetcher = new CookieHttpFetcher(
                new PluginSettings { CfClearanceCookie = "test-cookie" },
                new RateLimiter(TimeSpan.Zero),
                Logs.Add,
                () => { },
                Handler,
                [TimeSpan.Zero, TimeSpan.Zero]);
            Plugin.InitializeForTests(new TestPluginHost(_storageDirectory, Logs.Add), fetcher);
        }

        public BepisDbPlugin Plugin { get; } = new();
        public SequenceHttpMessageHandler Handler { get; }
        public List<string> Logs { get; } = [];

        public Task<ArtworkInfo?> FetchArtworkAsync(CancellationToken cancellationToken = default)
            => Plugin.FetchArtworkInfoAsync(
                new ArtworkId(BepisDbFilenameParser.ProviderId, "KKSCENE_123"),
                cancellationToken,
                saveToLocalCache: true);

        public void Dispose()
        {
            Plugin.Dispose();
            TryDeleteDirectory(_storageDirectory);
        }
    }

    private sealed class SequenceHttpMessageHandler(
        IEnumerable<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("The test issued more HTTP requests than expected.");
            return _responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class TestPluginHost(string storageDirectory, Action<string> log) : IPluginHost
    {
        public string StorageDirectory { get; } = storageDirectory;

        public void Log(string message) => log(message);
    }
}
