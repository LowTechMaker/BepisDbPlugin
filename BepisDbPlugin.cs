using System.Collections.Concurrent;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

public sealed class BepisDbPlugin : ICardImportProvider, IDisposable
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(5);

    private IPluginHost? _host;
    private IBepisDbFetcher? _fetcher;
    private ArtworkDiskCache? _artworkCache;

    private readonly ConcurrentDictionary<string, Lazy<Task<ArtworkInfo?>>> _artworkInFlight = new();
    private readonly ConcurrentDictionary<string, ArtworkDiskCache.CachedArtwork> _unsavedArtworkDetails = new();

    public string Name => "BepisDB";

    public string Version => typeof(BepisDbPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string ProviderId => BepisDbFilenameParser.ProviderId;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _artworkCache = new ArtworkDiskCache(host.StorageDirectory, host.Log);
        var settings = PluginSettings.Load(host.StorageDirectory, host.Log);

        var rateLimiter = new RateLimiter(MinRequestInterval, MaxJitter);

        if (settings.FetchStrategy.Equals("webview2", StringComparison.OrdinalIgnoreCase))
        {
            if (WebView2Fetcher.IsAvailable())
            {
                _fetcher = new WebView2Fetcher(host.StorageDirectory, rateLimiter, host.Log);
                host.Log("BepisDB: using WebView2 fetch strategy.");
            }
            else
            {
                host.Log("BepisDB: WebView2 runtime not found. Install the WebView2 Evergreen Runtime, " +
                         "or set fetchStrategy to \"cookie\" in settings.json. Falling back to cookie mode.");
                _fetcher = new CookieHttpFetcher(settings, rateLimiter, host.Log);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(settings.CfClearanceCookie))
            {
                host.Log("BepisDB: cookie strategy selected but no cf_clearance cookie configured. " +
                         "Edit settings.json in the plugin storage directory to add your cookie. " +
                         "Fetch requests will likely fail with 403.");
            }
            _fetcher = new CookieHttpFetcher(settings, rateLimiter, host.Log);
        }
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
            entry.UploaderName ?? "Unknown",
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
