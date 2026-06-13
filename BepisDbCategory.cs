namespace SceneGallery.Plugin.BepisDb;

internal enum BepisDbCategory
{
    KkScene,
    KkClothing,
    Koikatsu,
}

internal static class BepisDbCategoryHelper
{
    public static BepisDbCategory? TryFromPrefix(string prefix) => prefix switch
    {
        "KKSCENE" => BepisDbCategory.KkScene,
        "KKCLOTHING" => BepisDbCategory.KkClothing,
        "KK" => BepisDbCategory.Koikatsu,
        _ => null,
    };

    public static string ToUrlSegment(this BepisDbCategory category) => category switch
    {
        BepisDbCategory.KkScene => "kkscenes",
        BepisDbCategory.KkClothing => "kkclothing",
        BepisDbCategory.Koikatsu => "koikatsu",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    public static string ToCardType(this BepisDbCategory category) => category switch
    {
        BepisDbCategory.KkScene => "KKSCENE",
        BepisDbCategory.KkClothing => "KKCLOTHING",
        BepisDbCategory.Koikatsu => "KK",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    public static (BepisDbCategory Category, string NumericId)? ParseCompositeId(string compositeId)
    {
        var underscoreIndex = compositeId.LastIndexOf('_');
        if (underscoreIndex < 0) return null;

        var prefix = compositeId[..underscoreIndex];
        var numericId = compositeId[(underscoreIndex + 1)..];

        var category = TryFromPrefix(prefix);
        if (category is null || numericId.Length == 0) return null;

        return (category.Value, numericId);
    }
}
