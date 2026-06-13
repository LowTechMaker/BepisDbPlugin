namespace SceneGallery.Plugin.BepisDb;

internal interface IBepisDbFetcher : IDisposable
{
    Task<string?> FetchPageHtmlAsync(string url, CancellationToken ct);
}
