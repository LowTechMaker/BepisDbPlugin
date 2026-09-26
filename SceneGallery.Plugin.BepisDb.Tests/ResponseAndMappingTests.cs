using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class ResponseAndMappingTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"type\":\"success\",\"data\":{}}")]
    [InlineData("{\"type\":\"success\",\"data\":{\"card\":{\"id\":0,\"cardType\":\"KK\"}}}")]
    public void MalformedResponse_IsSchemaFailureWithoutInventingNotFound(string json)
    {
        var result = BepisDbResponseParser.ParseCard(json);
        Assert.Null(result.Card);
        Assert.NotNull(result.SchemaError);
    }

    [Fact]
    public void Mapping_PreservesAnonymousFallbackRatingAndTags()
    {
        var card = new BepisDbCardData
        {
            Id = 123, CardType = "KKSCENE", CustomName = "scene",
            Tags = [new BepisDbTag { Name = "tag" }, new BepisDbTag()],
        };
        var result = BepisDbCardMapper.ToArtworkInfo(new ArtworkId("bepisdb", "KKSCENE_123"),
            BepisDbCardMapper.ToCacheEntry(card), false)!;
        Assert.Equal("Anonymous", result.AuthorName);
        Assert.Equal("0", result.AuthorId);
        Assert.Equal(ContentRating.AllAges, result.Rating);
        Assert.Equal("tag", Assert.Single(result.Tags).Name);
        Assert.False(result.IsSavedLocally);
    }
}
