namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class BepisDbAuthorFolderNameParserTests
{
    [Theory]
    [InlineData("Alice (000123)", "123", "Alice")]
    [InlineData("作者（42）", "42", "作者")]
    [InlineData("Artist [000]", "0", "Artist")]
    public void TryParse_ExtractsNormalizedAuthor(string folder, string expectedId, string expectedName)
    {
        var result = BepisDbAuthorFolderNameParser.TryParse(folder);

        Assert.NotNull(result);
        Assert.Equal(BepisDbFilenameParser.ProviderId, result.Key.ProviderId);
        Assert.Equal(expectedId, result.Key.Id);
        Assert.Equal(expectedName, result.FolderDisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alice")]
    [InlineData("Alice (1234567890123)")]
    public void TryParse_InvalidFolder_ReturnsNull(string folder)
        => Assert.Null(BepisDbAuthorFolderNameParser.TryParse(folder));
}
