using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

internal static class BepisDbCardMapper
{
    internal static string GetProfileUrl(AuthorKey key) => $"https://db.bepis.moe/user/{key.Id}";

    internal static string GetArtworkUrl(ArtworkId id)
    {
        var parsed = BepisDbCategoryHelper.ParseCompositeId(id.Id);
        return parsed is null
            ? $"https://db.bepis.moe/koikatsu/view/{id.Id}"
            : $"https://db.bepis.moe/{parsed.Value.Category.ToUrlSegment()}/view/{parsed.Value.NumericId}";
    }

    internal static ArtworkDiskCache.CachedArtwork ToCacheEntry(BepisDbCardData card) => new(
        card.Uploader?.Username,
        card.Uploader?.Id.ToString(),
        card.CustomName,
        card.CardType,
        card.Tags?.Where(tag => tag.Name is not null).Select(tag => new ArtworkDiskCache.CachedTag(tag.Name!)).ToList(),
        card.DownloadCount,
        DateTimeOffset.UtcNow,
        Failed: false);

    internal static ArtworkInfo? ToArtworkInfo(ArtworkId id, ArtworkDiskCache.CachedArtwork entry, bool isSavedLocally)
    {
        if (entry.Failed) return null;
        var tags = entry.Tags?.Select(tag => new ArtworkTag(tag.Name, null)).ToList() as IReadOnlyList<ArtworkTag> ?? [];
        return new ArtworkInfo(id, entry.UploaderName ?? "Anonymous", entry.UploaderId ?? "0", entry.Title,
            null, ContentRating.AllAges, tags, entry.FetchedAt, isSavedLocally);
    }
}
