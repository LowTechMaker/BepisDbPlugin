using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

/// <summary>Composition root and sole lifetime owner. Dependencies never refer back to this type.</summary>
internal sealed class BepisDbRuntime : IDisposable
{
    private readonly object _settingsGate = new();
    private readonly string _storageDirectory;
    private readonly Action<string> _log;
    private readonly PluginSettings _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ArtworkDiskCache _cache;
    private readonly BepisDbSessionOwner _sessions;
    private readonly BepisDbFetchCoordinator _coordinator;
    private readonly PluginOperationDrain _drain;

    internal BepisDbRuntime(IPluginHost host,
        Func<PluginSettings, Action, IBepisDbFetcher>? fetcherFactory = null, TimeSpan? disposeTimeout = null)
    {
        _storageDirectory = host.StorageDirectory;
        _log = host.Log;
        _settings = PluginSettings.Load(_storageDirectory, _log);
        _cache = new ArtworkDiskCache(_storageDirectory, _log);
        var rateLimiter = new RateLimiter(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        try
        {
            _sessions = new BepisDbSessionOwner(_settings,
                fetcherFactory ?? ((settings, challenge) => new CookieHttpFetcher(settings, rateLimiter, _log, challenge)));
        }
        catch
        {
            try { _cache.Dispose(); } finally { _shutdown.Dispose(); }
            throw;
        }
        _drain = new PluginOperationDrain(nameof(BepisDbPlugin), disposeTimeout ?? TimeSpan.FromSeconds(10),
            _shutdown.Cancel,
            _ => { _sessions.Dispose(); return Task.CompletedTask; },
            () => { try { _cache.Dispose(); } finally { _shutdown.Dispose(); } },
            count => _log($"BepisDB shutdown timed out with {count} active producer(s); deferring cache disposal."),
            ex => _log($"BepisDB shutdown failed: {ex.GetType().Name}"));
        _coordinator = new BepisDbFetchCoordinator(_sessions, _cache, _log,
            producer => _drain.RunProducerAsync(producer), _shutdown.Token);
        if (NeedsCookieSetup)
            _log("BepisDB: no cf_clearance cookie configured. Use the cookie setup button on the import page.");
    }

    internal string DestinationFolderName { get { lock (_settingsGate) return _settings.DestinationFolderName; } }
    internal bool NeedsCookieSetup => _sessions.NeedsCookieSetup;
    internal Task Completion => _drain.Completion;

    internal void ApplyCookies(IReadOnlyDictionary<string, string> cookies, string userAgent)
        => RunSynchronous(() =>
        {
            lock (_settingsGate)
            {
                var hasClearance = cookies.TryGetValue("cf_clearance", out var clearance);
                if (hasClearance) _settings.CfClearanceCookie = clearance;
                _settings.UserAgent = userAgent;
                _sessions.Replace(_settings, resetCookieSetup: hasClearance);
                _settings.Save(_storageDirectory, _log);
            }
            _log("BepisDB: cookies updated from browser setup.");
            return true;
        });

    internal void SetSettingValue(string key, string? value) => RunSynchronous(() =>
    {
        lock (_settingsGate)
        {
            if (key == "destinationFolderName") _settings.DestinationFolderName = value?.Trim() ?? "";
            _settings.Save(_storageDirectory, _log);
        }
        return true;
    });

    internal Task<bool> HasUsableCookiesAsync(CancellationToken ct) => _drain.RunProducerAsync(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        using var lease = _sessions.Acquire();
        if (!lease.HasCookie)
        {
            _sessions.SetValidationResult(lease, false);
            return false;
        }
        var usable = await lease.Fetcher.HasUsableCookiesAsync(linked.Token).ConfigureAwait(false);
        _sessions.SetValidationResult(lease, usable);
        return usable;
    });

    internal Task<AuthorInfo?> GetAuthorInfoAsync(AuthorKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RunSynchronous(() => _coordinator.GetAuthorInfo(key)));
    }

    internal Task<ArtworkInfo?> FetchArtworkInfoAsync(ArtworkId id, CancellationToken ct, bool save)
        => RunSynchronous(() => _coordinator.FetchArtworkInfoAsync(id, save)).WaitAsync(ct);

    private T RunSynchronous<T>(Func<T> operation)
        => _drain.RunProducerAsync(() => Task.FromResult(operation())).GetAwaiter().GetResult();

    public void Dispose() => _drain.Dispose();
}
