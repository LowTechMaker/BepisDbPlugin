using System.Collections.Concurrent;
using System.Text.Json;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class ArtworkDiskCache : IDisposable
{
    public sealed record CachedArtwork(
        string? UploaderName,
        string? UploaderId,
        string? Title,
        string? CardType,
        IReadOnlyList<CachedTag>? Tags,
        int DownloadCount,
        DateTimeOffset FetchedAt,
        bool Failed);

    public sealed record CachedTag(string Name);

    private static readonly TimeSpan FailedEntryTtl = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, CachedArtwork> _entries = new();
    private readonly string _cachePath;
    private readonly Action<string> _log;
    private readonly DebouncedDiskPersistence _persistence;

    public ArtworkDiskCache(string storageDirectory, Action<string> log)
        : this(storageDirectory, log, AtomicFileWriter.Write)
    {
    }

    internal ArtworkDiskCache(
        string storageDirectory,
        Action<string> log,
        Action<string, Action<Stream>> writeAtomically)
    {
        _cachePath = Path.Combine(storageDirectory, "artworks.json");
        _log = log;
        _persistence = new DebouncedDiskPersistence(
            _cachePath,
            stream => JsonSerializer.Serialize(stream, _entries),
            ex => _log($"artwork cache save failed: {ex.Message}"),
            writeAtomically);
        Load();
    }

    public bool TryGet(string artworkId, out CachedArtwork entry)
    {
        if (!_entries.TryGetValue(artworkId, out entry!)) return false;
        if (entry.Failed && DateTimeOffset.UtcNow - entry.FetchedAt > FailedEntryTtl)
        {
            _entries.TryRemove(artworkId, out _);
            return false;
        }
        return true;
    }

    public void Set(string artworkId, CachedArtwork entry)
    {
        _entries[artworkId] = entry;
        _persistence.MarkDirty();
    }

    internal bool IsDirty => _persistence.IsDirty;

    internal TimeSpan RetryDelay => _persistence.RetryDelay;

    public CachedArtwork? FindByUploaderId(string uploaderId)
        => _entries.Values
            .Where(e => !e.Failed && e.UploaderId == uploaderId && e.UploaderName is not null)
            .OrderByDescending(e => e.FetchedAt)
            .FirstOrDefault();

    public IReadOnlyList<ArtworkTag>? GetCachedTags(string artworkId)
    {
        if (!_entries.TryGetValue(artworkId, out var entry) || entry.Tags is null)
            return null;
        return entry.Tags.Select(t => new ArtworkTag(t.Name, null)).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            using var stream = File.OpenRead(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedArtwork>>(stream);
            if (loaded is null) return;
            foreach (var (id, entry) in loaded)
                _entries[id] = entry;
        }
        catch (Exception ex)
        {
            _log($"artwork cache unreadable, starting fresh: {ex.Message}");
        }
    }

    internal void Flush() => _persistence.Flush();

    public void Dispose() => _persistence.Dispose();
}
