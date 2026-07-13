namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class BepisDbCategoryTests
{
    [Theory]
    [InlineData("KKSCENE", "KkScene", "kkscenes", "KKSCENE")]
    [InlineData("KKCLOTHING", "KkClothing", "kkclothing", "KKCLOTHING")]
    [InlineData("KK", "Koikatsu", "koikatsu", "KK")]
    public void CategoryMappings_RoundTrip(
        string prefix,
        string expected,
        string expectedUrlSegment,
        string expectedCardType)
    {
        var category = BepisDbCategoryHelper.TryFromPrefix(prefix);

        Assert.Equal(expected, category?.ToString());
        Assert.Equal(expectedUrlSegment, category!.Value.ToUrlSegment());
        Assert.Equal(expectedCardType, category.Value.ToCardType());
    }

    [Fact]
    public void ParseCompositeId_SplitsAtLastUnderscore()
    {
        var result = BepisDbCategoryHelper.ParseCompositeId("KKSCENE_00789");

        Assert.NotNull(result);
        Assert.Equal(BepisDbCategory.KkScene, result.Value.Category);
        Assert.Equal("00789", result.Value.NumericId);
    }

    [Theory]
    [InlineData("UNKNOWN_1")]
    [InlineData("KKSCENE")]
    [InlineData("KKSCENE_")]
    public void ParseCompositeId_InvalidValue_ReturnsNull(string value)
        => Assert.Null(BepisDbCategoryHelper.ParseCompositeId(value));
}
