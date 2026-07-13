namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_SerializesCallersAndHonorsCancellation()
    {
        var limiter = new RateLimiter(TimeSpan.Zero);
        using var first = await limiter.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Dispose_ReleasesGateOnlyOnce()
    {
        var limiter = new RateLimiter(TimeSpan.Zero);
        var first = await limiter.AcquireAsync(CancellationToken.None);

        first.Dispose();
        first.Dispose();
        using var second = await limiter.AcquireAsync(CancellationToken.None);
    }
}
