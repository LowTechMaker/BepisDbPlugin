namespace SceneGallery.Plugin.BepisDb;

internal interface IBepisDbFetcher : IDisposable
{
    Task<BepisDbCardData?> FetchCardAsync(string cardType, string numericId, CancellationToken ct);
}
