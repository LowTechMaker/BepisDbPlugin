namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class BepisDbFilenameParserTests
{
    [Theory]
    [InlineData("prefix_KKSCENE_078928.png", "KKSCENE_78928")]
    [InlineData("KKCLOTHING_00042", "KKCLOTHING_42")]
    [InlineData("KK_000", "KK_0")]
    public void TryParse_NormalizesCompositeId(string fileName, string expectedId)
    {
        var result = BepisDbFilenameParser.TryParse(fileName);

        Assert.NotNull(result);
        Assert.Equal(BepisDbFilenameParser.ProviderId, result.ProviderId);
        Assert.Equal(expectedId, result.Id);
    }

    [Theory]
    [InlineData("https://db.bepis.moe/kkscenes/view/00012", "KKSCENE_12")]
    [InlineData("https://db.bepis.moe/kkclothing/view/44", "KKCLOTHING_44")]
    [InlineData("https://db.bepis.moe/koikatsu/view/9", "KK_9")]
    public void TryParseUrl_MapsCategory(string url, string expectedId)
        => Assert.Equal(expectedId, BepisDbFilenameParser.TryParseUrl(url)?.Id);

    [Fact]
    public void TryParseFolder_ReadsTrailingFullWidthBrackets()
        => Assert.Equal(
            "KKSCENE_123",
            BepisDbFilenameParser.TryParseFolder("Artist［KKSCENE_000123］")?.Id);

    [Theory]
    [InlineData("")]
    [InlineData("KKSCENE_no-number")]
    [InlineData("https://example.test/kkscenes/view/12")]
    public void InvalidInput_ReturnsNull(string value)
    {
        Assert.Null(BepisDbFilenameParser.TryParse(value));
        Assert.Null(BepisDbFilenameParser.TryParseUrl(value));
    }
}
