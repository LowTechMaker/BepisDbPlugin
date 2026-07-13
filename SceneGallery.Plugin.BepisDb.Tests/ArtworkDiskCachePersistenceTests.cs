using System.Text.Json;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class ArtworkDiskCachePersistenceTests
{
    [Fact]
    public void FailedWrite_KeepsDirtyAndDoublesBackoff()
    {
        using var directory = new CacheTempDirectory();
        var logs = new List<string>();
        using var cache = new ArtworkDiskCache(
            directory.Path,
            logs.Add,
            (_, _) => throw new IOException("disk unavailable"));
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(5), cache.RetryDelay);
        cache.Flush();
        Assert.True(cache.IsDirty);
        Assert.Equal(TimeSpan.FromSeconds(10), cache.RetryDelay);
        for (var i = 0; i < 5; i++) cache.Flush();
        Assert.Equal(TimeSpan.FromMinutes(5), cache.RetryDelay);
        Assert.Contains(logs, message => message.Contains("disk unavailable"));
    }

    [Fact]
    public void RetryAfterFailure_PersistsAndResetsBackoff()
    {
        using var directory = new CacheTempDirectory();
        var attempts = 0;
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            if (++attempts == 1) throw new IOException("first attempt fails");
            WriteAtomically(path, serialize);
        });
        cache.Set("1", Entry("Recovered"));

        cache.Flush();
        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal(TimeSpan.Zero, cache.RetryDelay);
        Assert.Equal(2, attempts);
        Assert.Equal("Recovered", Read(directory.Path, "1").UploaderName);
    }

    [Fact]
    public void SuccessfulWrite_ClearsDirty()
    {
        using var directory = new CacheTempDirectory();
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, WriteAtomically);
        cache.Set("1", Entry("Artist"));

        cache.Flush();

        Assert.False(cache.IsDirty);
        Assert.Equal("Artist", Read(directory.Path, "1").UploaderName);
    }

    [Fact]
    public void MutationDuringWrite_KeepsDirtyUntilLatestGenerationPersists()
    {
        using var directory = new CacheTempDirectory();
        var attempts = 0;
        ArtworkDiskCache? target = null;
        using var cache = new ArtworkDiskCache(directory.Path, _ => { }, (path, serialize) =>
        {
            WriteAtomically(path, serialize);
            if (++attempts == 1)
                target!.Set("1", Entry("New"));
        });
        target = cache;
        cache.Set("1", Entry("Old"));

        cache.Flush();

        Assert.True(cache.IsDirty);
        Assert.Equal("Old", Read(directory.Path, "1").UploaderName);
        cache.Flush();
        Assert.False(cache.IsDirty);
        Assert.Equal("New", Read(directory.Path, "1").UploaderName);
    }

    private static ArtworkDiskCache.CachedArtwork Entry(string uploaderName)
        => new(uploaderName, "42", "Title", "KKSCENE", [], 1, DateTimeOffset.UtcNow, Failed: false);

    private static ArtworkDiskCache.CachedArtwork Read(string directory, string id)
    {
        var json = File.ReadAllText(Path.Combine(directory, "artworks.json"));
        return JsonSerializer.Deserialize<Dictionary<string, ArtworkDiskCache.CachedArtwork>>(json)![id];
    }

    private static void WriteAtomically(string path, Action<Stream> serialize)
    {
        var tempPath = path + ".test.tmp";
        using (var stream = File.Create(tempPath))
            serialize(stream);
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed class CacheTempDirectory : IDisposable
    {
        public CacheTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SceneGallery.Plugin.BepisDb.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for a test-only temporary directory.
            }
        }
    }
}
