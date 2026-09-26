using System.Collections.Concurrent;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

/// <summary>Owns provider cache policy and shared fetches; never owns an SDK host or HTTP transport.</summary>
internal sealed class BepisDbFetchCoordinator(
    BepisDbSessionOwner sessions,
    ArtworkDiskCache cache,
    Action<string> log,
    Func<Func<Task<ArtworkInfo?>>, Task<ArtworkInfo?>> runProducer,
    CancellationToken shutdownToken)
{
    private readonly ConcurrentDictionary<FetchKey, Lazy<Task<ArtworkInfo?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, ArtworkDiskCache.CachedArtwork> _unsaved = new();

    internal AuthorInfo? GetAuthorInfo(AuthorKey key)
    {
        if (key.ProviderId != BepisDbFilenameParser.ProviderId) return null;
        var entry = cache.FindByUploaderId(key.Id);
        return entry?.UploaderName is null ? null
            : new AuthorInfo(key, entry.UploaderName, null, BepisDbCardMapper.GetProfileUrl(key), entry.FetchedAt);
    }

    // The runtime admits this synchronous cache/read/start step before calling it.
    // Only the winning lazy starts a tracked producer; caller tokens never enter that producer.
    internal Task<ArtworkInfo?> FetchArtworkInfoAsync(ArtworkId id, bool saveToLocalCache)
    {
        if (id.ProviderId != BepisDbFilenameParser.ProviderId) return Task.FromResult<ArtworkInfo?>(null);
        if (cache.TryGet(id.Id, out var cached) && cached.Title is not null)
            return Task.FromResult(BepisDbCardMapper.ToArtworkInfo(id, cached, true));
        if (saveToLocalCache && _unsaved.TryRemove(id.Id, out var unsaved))
        {
            cache.Set(id.Id, unsaved);
            return Task.FromResult(BepisDbCardMapper.ToArtworkInfo(id, unsaved, true));
        }

        var lease = sessions.Acquire();
        var key = new FetchKey(id.Id, saveToLocalCache, lease.GenerationId);
        Lazy<Task<ArtworkInfo?>> candidate = null!;
        candidate = new Lazy<Task<ArtworkInfo?>>(() => RunLeasedFetchAsync(id, key, lease, candidate));
        var selected = _inFlight.GetOrAdd(key, candidate);
        if (!ReferenceEquals(candidate, selected)) lease.Dispose();
        return selected.Value;
    }

    private async Task<ArtworkInfo?> RunLeasedFetchAsync(ArtworkId id, FetchKey key,
        BepisDbSessionOwner.Lease lease, Lazy<Task<ArtworkInfo?>> identity)
    {
        try
        {
            return await runProducer(() => FetchAsync(id, key.Save, lease)).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
            _inFlight.TryRemove(new KeyValuePair<FetchKey, Lazy<Task<ArtworkInfo?>>>(key, identity));
        }
    }

    private async Task<ArtworkInfo?> FetchAsync(ArtworkId id, bool save, BepisDbSessionOwner.Lease lease)
    {
        try
        {
            var parsed = BepisDbCategoryHelper.ParseCompositeId(id.Id);
            if (parsed is null)
            {
                log($"Cannot parse composite ID: {id.Id}");
                return null;
            }
            var card = await lease.Fetcher.FetchCardAsync(parsed.Value.Category.ToCardType(),
                parsed.Value.NumericId, shutdownToken).ConfigureAwait(false);
            if (card is null) return null;
            var entry = BepisDbCardMapper.ToCacheEntry(card);
            var published = sessions.TryPublish(lease, () =>
            {
                if (save)
                {
                    cache.Set(id.Id, entry);
                    _unsaved.TryRemove(id.Id, out _);
                }
                else
                    _unsaved[id.Id] = entry;
            });
            return BepisDbCardMapper.ToArtworkInfo(id, entry, save && published);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            log($"fetch failed for BepisDB artwork {id.Id}: {ex.Message}");
            return null;
        }
    }

    private readonly record struct FetchKey(string Id, bool Save, long Generation);
}
