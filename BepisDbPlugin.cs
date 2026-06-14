using System.Collections.Concurrent;
using System.Reflection;
using SceneGallery.PluginSdk;

[assembly: AssemblyMetadata("PluginDescription", "Imports card metadata from BepisDB (db.bepis.moe)")]

namespace SceneGallery.Plugin.BepisDb;

public sealed class BepisDbPlugin : IFolderAuthorProvider, ICardImportProvider, IImportDestinationProvider, ICookieSetupValidator, IPluginSettingsProvider, IDisposable
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(5);

    private IPluginHost? _host;
    private PluginSettings? _settings;
    private RateLimiter? _rateLimiter;
    private IBepisDbFetcher? _fetcher;
    private ArtworkDiskCache? _artworkCache;
    private bool _cookieSetupRequired;

    private readonly ConcurrentDictionary<string, Lazy<Task<ArtworkInfo?>>> _artworkInFlight = new();
    private readonly ConcurrentDictionary<string, ArtworkDiskCache.CachedArtwork> _unsavedArtworkDetails = new();

    public string Name => "BepisDB";

    public string Version => typeof(BepisDbPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string ProviderId => BepisDbFilenameParser.ProviderId;

    public string DestinationFolderName => _settings?.DestinationFolderName ?? "BepisDB";

    public bool UsesRatingFolders => false;

    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new(
            "destinationFolderName",
            "Destination folder",
            "Folder inserted below the organized import subfolder. Leave empty to skip the provider folder.",
            PluginSettingValueType.Text,
            "BepisDB"),
    ];

    // ICookieSetupProvider
    public string SetupUrl => "https://db.bepis.moe/";
    public string CookieDomain => "db.bepis.moe";
    public string CompletionTitleHint => "BepisDB";
    public bool NeedsCookieSetup => _cookieSetupRequired || string.IsNullOrEmpty(_settings?.CfClearanceCookie);

    public void ApplyCookies(IReadOnlyDictionary<string, string> cookies, string userAgent)
    {
        if (_host is null || _settings is null) return;

        if (cookies.TryGetValue("cf_clearance", out var clearance))
        {
            _settings.CfClearanceCookie = clearance;
            _cookieSetupRequired = false;
        }
        _settings.UserAgent = userAgent;
        _settings.Save(_host.StorageDirectory, _host.Log);

        _fetcher?.Dispose();
        _fetcher = CreateCookieFetcher();
        _host.Log("BepisDB: cookies updated from browser setup.");
    }

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _artworkCache = new ArtworkDiskCache(host.StorageDirectory, host.Log);
        _settings = PluginSettings.Load(host.StorageDirectory, host.Log);
        _rateLimiter = new RateLimiter(MinRequestInterval, MaxJitter);

        if (NeedsCookieSetup)
            host.Log("BepisDB: no cf_clearance cookie configured. Use the cookie setup button on the import page.");

        _fetcher = CreateCookieFetcher();
    }

    public string? GetSettingValue(string key) => key switch
    {
        "destinationFolderName" => DestinationFolderName,
        _ => null,
    };

    public void SetSettingValue(string key, string? value)
    {
        if (_host is null || _settings is null)
            return;

        switch (key)
        {
            case "destinationFolderName":
                _settings.DestinationFolderName = value?.Trim() ?? "";
                break;
        }

        _settings.Save(_host.StorageDirectory, _host.Log);
    }

    public async Task<bool> HasUsableCookiesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings?.CfClearanceCookie))
        {
            _cookieSetupRequired = true;
            return false;
        }

        if (_fetcher is not CookieHttpFetcher cookieFetcher)
            return true;

        var usable = await cookieFetcher.HasUsableCookiesAsync(ct).ConfigureAwait(false);
        _cookieSetupRequired = !usable;
        return usable;
    }

    private CookieHttpFetcher CreateCookieFetcher()
        => new(_settings!, _rateLimiter!, _host!.Log, () => _cookieSetupRequired = true);

    public ParsedAuthor? TryParseFolderName(string folderName)
        => BepisDbAuthorFolderNameParser.TryParse(folderName);

    public string GetProfileUrl(AuthorKey key)
        => $"https://db.bepis.moe/user/{key.Id}";

    public Task<AuthorInfo?> GetAuthorInfoAsync(AuthorKey key, bool forceRefresh, CancellationToken ct)
    {
        if (_artworkCache is null || key.ProviderId != ProviderId)
            return Task.FromResult<AuthorInfo?>(null);

        var cached = _artworkCache.FindByUploaderId(key.Id);
        if (cached is null || cached.UploaderName is null)
            return Task.FromResult<AuthorInfo?>(null);

        return Task.FromResult<AuthorInfo?>(new AuthorInfo(
            key,
            cached.UploaderName,
            null,
            GetProfileUrl(key),
            cached.FetchedAt));
    }

    public ArtworkId? TryParseFilename(string fileName)
        => BepisDbFilenameParser.TryParse(fileName);

    public ArtworkId? TryParseArtworkFolderName(string folderName)
        => BepisDbFilenameParser.TryParseFolder(folderName);

    public string GetArtworkUrl(ArtworkId id)
    {
        var parsed = BepisDbCategoryHelper.ParseCompositeId(id.Id);
        if (parsed is null) return $"https://db.bepis.moe/koikatsu/view/{id.Id}";
        return $"https://db.bepis.moe/{parsed.Value.Category.ToUrlSegment()}/view/{parsed.Value.NumericId}";
    }

    public Task<ArtworkInfo?> FetchArtworkInfoAsync(
        ArtworkId id,
        CancellationToken ct,
        bool saveToLocalCache = true)
    {
        if (_fetcher is null || _artworkCache is null || id.ProviderId != ProviderId)
            return Task.FromResult<ArtworkInfo?>(null);

        if (_artworkCache.TryGet(id.Id, out var cached) && (cached.Failed || cached.Title != null))
            return Task.FromResult(ToArtworkInfo(id, cached, isSavedLocally: true));

        if (saveToLocalCache && _unsavedArtworkDetails.TryRemove(id.Id, out var unsaved))
        {
            _artworkCache.Set(id.Id, unsaved);
            return Task.FromResult(ToArtworkInfo(id, unsaved, isSavedLocally: true));
        }

        var inFlightKey = $"{id.Id}:{saveToLocalCache}";
        var lazy = _artworkInFlight.GetOrAdd(inFlightKey, _ => new Lazy<Task<ArtworkInfo?>>(
            () => FetchArtworkAsync(id, saveToLocalCache, ct)));
        return lazy.Value;
    }

    private async Task<ArtworkInfo?> FetchArtworkAsync(
        ArtworkId id,
        bool saveToLocalCache,
        CancellationToken ct)
    {
        try
        {
            var parsed = BepisDbCategoryHelper.ParseCompositeId(id.Id);
            if (parsed is null)
            {
                _host?.Log($"Cannot parse composite ID: {id.Id}");
                return null;
            }

            var card = await _fetcher!.FetchCardAsync(
                parsed.Value.Category.ToCardType(), parsed.Value.NumericId, ct).ConfigureAwait(false);

            if (card is null)
            {
                if (saveToLocalCache)
                {
                    _artworkCache!.Set(id.Id, new ArtworkDiskCache.CachedArtwork(
                        null, null, null, null, null, 0, DateTimeOffset.UtcNow, Failed: true));
                }
                return null;
            }

            var tags = card.Tags?
                .Where(t => t.Name is not null)
                .Select(t => new ArtworkDiskCache.CachedTag(t.Name!))
                .ToList();

            var entry = new ArtworkDiskCache.CachedArtwork(
                card.Uploader?.Username,
                card.Uploader?.Id.ToString(),
                card.CustomName,
                card.CardType,
                tags,
                card.DownloadCount,
                DateTimeOffset.UtcNow,
                Failed: false);

            if (saveToLocalCache)
            {
                _artworkCache!.Set(id.Id, entry);
                _unsavedArtworkDetails.TryRemove(id.Id, out _);
            }
            else
            {
                _unsavedArtworkDetails[id.Id] = entry;
            }

            return ToArtworkInfo(id, entry, saveToLocalCache);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _host?.Log($"fetch failed for BepisDB artwork {id.Id}: {ex.Message}");
            return null;
        }
        finally
        {
            _artworkInFlight.TryRemove($"{id.Id}:{saveToLocalCache}", out _);
        }
    }

    private static ArtworkInfo? ToArtworkInfo(
        ArtworkId id,
        ArtworkDiskCache.CachedArtwork entry,
        bool isSavedLocally)
    {
        if (entry.Failed) return null;

        var tags = entry.Tags?
            .Select(t => new ArtworkTag(t.Name, null))
            .ToList() as IReadOnlyList<ArtworkTag>
            ?? [];

        return new ArtworkInfo(
            id,
            entry.UploaderName ?? "Anonymous",
            entry.UploaderId ?? "0",
            entry.Title,
            null,
            ContentRating.AllAges,
            tags,
            entry.FetchedAt,
            isSavedLocally);
    }

    public void Dispose()
    {
        _artworkCache?.Dispose();
        _fetcher?.Dispose();
    }
}
