using System.Collections.Concurrent;
using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class PluginLifecycleTests
{
    [Fact]
    public async Task DisposeBeforeInitialize_RejectsStatefulFallbacks()
    {
        using var plugin = new BepisDbPlugin();
        plugin.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.FetchArtworkInfoAsync(
            new ArtworkId("bepisdb", "KKSCENE_123"), CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.GetAuthorInfoAsync(
            new AuthorKey("bepisdb", "5"), false, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.HasUsableCookiesAsync(CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => plugin.ApplyCookies(new Dictionary<string, string>(), "agent"));
        Assert.Throws<ObjectDisposedException>(() => plugin.SetSettingValue("destinationFolderName", "new"));
        Assert.Throws<ObjectDisposedException>(() => plugin.GetSettingValue("destinationFolderName"));
    }

    [Fact]
    public async Task BeforeInitialize_PreservesOrdinaryFallbacks()
    {
        using var plugin = new BepisDbPlugin();
        Assert.Null(await plugin.FetchArtworkInfoAsync(new ArtworkId("bepisdb", "KKSCENE_123"), CancellationToken.None));
        Assert.Null(await plugin.GetAuthorInfoAsync(new AuthorKey("bepisdb", "5"), false, CancellationToken.None));
        Assert.False(await plugin.HasUsableCookiesAsync(CancellationToken.None));
        plugin.ApplyCookies(new Dictionary<string, string>(), "agent");
        plugin.SetSettingValue("destinationFolderName", "ignored");
        Assert.Equal("BepisDB", plugin.GetSettingValue("destinationFolderName"));
        Assert.Null(plugin.GetSettingValue("unknown"));
        Assert.True(plugin.NeedsCookieSetup);
    }

    [Fact]
    public void DisposeBeforeInitialize_RejectsInitializationWithoutConstructingTransport()
    {
        using var plugin = new BepisDbPlugin();
        plugin.Dispose();
        Assert.Throws<ObjectDisposedException>(() => plugin.InitializeForTests(new Host("", _ => { }),
            (_, _) => throw new InvalidOperationException("A disposed plugin must not construct a transport.")));
    }

    [Fact]
    public void InitializedPlugin_RejectsRepeatedInitializationWithoutReplacingItsOwner()
    {
        using var context = new Context();
        var constructed = false;
        Assert.Throws<InvalidOperationException>(() => context.Plugin.InitializeForTests(new Host(context.Directory, _ => { }),
            (_, callback) => { constructed = true; return new ControlledFetcher(callback); }));
        Assert.False(constructed);
        Assert.Equal(0, context.Fetchers[0].DisposeCount);
        context.Plugin.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.Plugin.InitializeForTests(new Host(context.Directory, _ => { }),
            (_, callback) => { constructed = true; return new ControlledFetcher(callback); }));
        Assert.False(constructed);
        Assert.Equal(1, context.Fetchers[0].DisposeCount);
    }

    [Fact]
    public async Task CookieReplacement_KeepsOldFetcherAliveUntilItsProducerFinishes()
    {
        using var context = new Context();
        var old = context.Fetchers[0];
        var pending = context.FetchAsync();
        context.ReplaceCookies();
        try
        {
            Assert.Equal(0, old.DisposeCount);
        }
        finally
        {
            old.Card.TrySetResult(Card("old"));
        }
        Assert.NotNull(await pending);
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(0, context.Fetchers[1].DisposeCount);
    }

    [Fact]
    public async Task CookieReplacement_NewCallerDoesNotJoinOldSessionFetch()
    {
        using var context = new Context();
        var old = context.Fetchers[0];
        var first = context.FetchAsync();
        context.ReplaceCookies();
        context.Fetchers[1].Card.SetResult(Card("new"));
        try
        {
            var second = await context.FetchAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("new", second?.Title);
            Assert.Equal(1, context.Fetchers[1].FetchCount);
        }
        finally
        {
            old.Card.TrySetResult(Card("old"));
            await first;
        }
        Assert.Equal("new", (await context.FetchAsync())?.Title);
    }

    [Fact]
    public async Task CookieReplacement_LateValidationCannotInvalidateNewCookies()
    {
        using var context = new Context();
        var old = context.Fetchers[0];
        var validation = context.Plugin.HasUsableCookiesAsync(CancellationToken.None);
        context.ReplaceCookies();
        old.Validation.SetResult(false);
        Assert.False(await validation);
        Assert.False(context.Plugin.NeedsCookieSetup);
    }

    [Fact]
    public void CookieReplacement_LateChallengeCannotInvalidateNewCookies()
    {
        using var context = new Context();
        var old = context.Fetchers[0];
        context.ReplaceCookies();
        old.MarkCookieSetupRequired();
        Assert.False(context.Plugin.NeedsCookieSetup);
    }

    [Fact]
    public async Task Dispose_ProducerOutlivingDeadlineStillPersistsItsFinalResult()
    {
        using var context = new Context();
        var pending = context.FetchAsync();
        context.Plugin.Dispose();
        Assert.Equal(1, context.Fetchers[0].DisposeCount);
        context.Fetchers[0].Card.SetResult(Card("completed after shutdown"));
        Assert.NotNull(await pending);
        var path = Path.Combine(context.Directory, "artworks.json");
        Assert.True(File.Exists(path), "The last producer must flush before cache disposal.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("completed after shutdown", document.RootElement.GetProperty("KKSCENE_123").GetProperty("Title").GetString());
    }

    [Fact]
    public async Task Dispose_RejectsNewProducers()
    {
        using var context = new Context();
        context.Plugin.Dispose();
        context.Fetchers[0].Card.SetResult(Card("unexpected"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.FetchAsync());
        Assert.Equal(0, context.Fetchers[0].FetchCount);
    }

    [Fact]
    public async Task PreviewThenSave_PromotesWithoutAnotherNetworkRequest()
    {
        using var context = new Context();
        context.Fetchers[0].Card.SetResult(Card("preview"));
        Assert.False((await context.FetchAsync(save: false))!.IsSavedLocally);
        Assert.False(File.Exists(Path.Combine(context.Directory, "artworks.json")));
        Assert.True((await context.FetchAsync(save: true))!.IsSavedLocally);
        Assert.Equal(1, context.Fetchers[0].FetchCount);
        context.Plugin.Dispose();
        Assert.Contains("preview", File.ReadAllText(Path.Combine(context.Directory, "artworks.json")));
    }

    [Fact]
    public async Task AuthorLookup_UsesCachedUploaderAndDoesNotFetchForForceRefresh()
    {
        using var context = new Context();
        context.Fetchers[0].Card.SetResult(Card("artwork"));
        await context.FetchAsync();
        var author = await context.Plugin.GetAuthorInfoAsync(new AuthorKey("bepisdb", "5"), true, CancellationToken.None);
        Assert.Equal("artist", author?.Name);
        Assert.Equal(1, context.Fetchers[0].FetchCount);
    }

    [Fact]
    public async Task CookieReplacement_MultipleRetirementsDisposeEachTransportExactlyOnce()
    {
        using var context = new Context();
        var pending = context.FetchAsync();
        context.ReplaceCookies();
        context.ReplaceCookies();
        Assert.Equal(0, context.Fetchers[0].DisposeCount);
        Assert.Equal(1, context.Fetchers[1].DisposeCount);
        Assert.Equal(0, context.Fetchers[2].DisposeCount);
        context.Fetchers[0].Card.SetResult(Card("retired"));
        Assert.False((await pending)!.IsSavedLocally);
        context.Plugin.Dispose();
        context.Plugin.Dispose();
        Assert.All(context.Fetchers, fetcher => Assert.Equal(1, fetcher.DisposeCount));
    }

    [Fact]
    public void CookieReplacement_PassesDetachedSettingsSnapshotsToTransport()
    {
        using var context = new Context();
        context.ReplaceCookies();
        Assert.Equal("initial-cookie", context.SettingsSnapshots[0].CfClearanceCookie);
        Assert.Equal("new-cookie", context.SettingsSnapshots[1].CfClearanceCookie);
    }

    [Fact]
    public void ApplyCookies_WithoutClearancePreservesExistingCookieAndSetupRequirement()
    {
        using var context = new Context();
        context.Fetchers[0].MarkCookieSetupRequired();
        context.Plugin.ApplyCookies(new Dictionary<string, string>(), "new-agent");
        Assert.Equal("initial-cookie", context.SettingsSnapshots[1].CfClearanceCookie);
        Assert.Equal("new-agent", context.SettingsSnapshots[1].UserAgent);
        Assert.True(context.Plugin.NeedsCookieSetup);
    }

    [Fact]
    public async Task OldCacheWithoutTitle_IsRefetchedAndKeepsExistingJsonSchema()
    {
        var legacy = new ArtworkDiskCache.CachedArtwork("old artist", "5", null, "KKSCENE", null, 0, DateTimeOffset.UtcNow, false);
        using var context = new Context(legacy);
        context.Fetchers[0].Card.SetResult(Card("refetched"));
        Assert.Equal("refetched", (await context.FetchAsync())?.Title);
        Assert.Equal(1, context.Fetchers[0].FetchCount);
        context.Plugin.Dispose();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(context.Directory, "artworks.json")));
        var entry = document.RootElement.GetProperty("KKSCENE_123");
        Assert.Equal("artist", entry.GetProperty("UploaderName").GetString());
        Assert.False(entry.GetProperty("Failed").GetBoolean());
    }

    [Fact]
    public void Dispose_RejectsCookieAndSettingsMutations()
    {
        using var context = new Context();
        context.Plugin.Dispose();
        Assert.Throws<ObjectDisposedException>(context.ReplaceCookies);
        Assert.Throws<ObjectDisposedException>(() => context.Plugin.SetSettingValue("destinationFolderName", "new"));
        Assert.Single(context.Fetchers);
    }

    internal static BepisDbCardData Card(string title) => new()
    {
        Id = 123,
        CardType = "KKSCENE",
        CustomName = title,
        Uploader = new BepisDbUploader { Id = 5, Username = "artist" },
    };

    private sealed class ControlledFetcher(Action markCookieSetupRequired) : IBepisDbFetcher
    {
        internal TaskCompletionSource<BepisDbCardData?> Card { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Validation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount;
        internal int FetchCount;
        internal Action MarkCookieSetupRequired { get; } = markCookieSetupRequired;

        public Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchCount);
            return Card.Task; // Intentionally ignores cancellation to model a delayed producer.
        }

        public Task<bool> HasUsableCookiesAsync(CancellationToken ct) => Validation.Task;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class Context : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "BepisLifecycleTests", Guid.NewGuid().ToString("N"));
        internal BepisDbPlugin Plugin { get; } = new();
        internal List<ControlledFetcher> Fetchers { get; } = [];
        internal List<PluginSettings> SettingsSnapshots { get; } = [];
        internal ConcurrentQueue<string> Logs { get; } = new();

        internal Context(ArtworkDiskCache.CachedArtwork? legacy = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "settings.json"), "{\"cfClearanceCookie\":\"initial-cookie\"}");
            if (legacy is not null)
                File.WriteAllText(Path.Combine(Directory, "artworks.json"), JsonSerializer.Serialize(new Dictionary<string, ArtworkDiskCache.CachedArtwork> { ["KKSCENE_123"] = legacy }));
            Plugin.InitializeForTests(new Host(Directory, Logs.Enqueue), (settings, challenge) =>
            {
                SettingsSnapshots.Add(settings);
                var fetcher = new ControlledFetcher(challenge);
                Fetchers.Add(fetcher);
                return fetcher;
            }, TimeSpan.FromMilliseconds(25));
        }

        internal Task<ArtworkInfo?> FetchAsync(bool save = true, CancellationToken ct = default)
            => Plugin.FetchArtworkInfoAsync(new ArtworkId("bepisdb", "KKSCENE_123"), ct, save);

        internal void ReplaceCookies() => Plugin.ApplyCookies(new Dictionary<string, string> { ["cf_clearance"] = "new-cookie" }, "test-agent");

        public void Dispose()
        {
            foreach (var fetcher in Fetchers)
            {
                fetcher.Card.TrySetResult(null);
                fetcher.Validation.TrySetResult(false);
            }
            Plugin.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Host(string storageDirectory, Action<string> log) : IPluginHost
    {
        public string StorageDirectory => storageDirectory;
        public void Log(string message) => log(message);
    }
}
