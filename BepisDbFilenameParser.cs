using System.Text.RegularExpressions;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

internal static partial class BepisDbFilenameParser
{
    public const string ProviderId = "bepisdb";

    [GeneratedRegex(@"(KKSCENE|KKCLOTHING|KK)_(\d+)", RegexOptions.Compiled)]
    private static partial Regex Pattern();

    public static ArtworkId? TryParse(string fileName)
    {
        var match = Pattern().Match(fileName);
        if (!match.Success) return null;

        var prefix = match.Groups[1].Value;
        if (BepisDbCategoryHelper.TryFromPrefix(prefix) is null) return null;

        // Strip leading zeros: KKSCENE_078928 → KKSCENE_78928
        var numericId = match.Groups[2].Value.TrimStart('0');
        if (numericId.Length == 0) numericId = "0";

        return new ArtworkId(ProviderId, $"{prefix}_{numericId}");
    }

    public static ArtworkId? TryParseFolder(string folderName)
    {
        var direct = TryParse(folderName);
        if (direct is not null) return direct;

        var bracketMatch = BracketPattern().Match(folderName);
        if (!bracketMatch.Success) return null;

        var inner = bracketMatch.Groups[1].Value;
        return TryParse(inner);
    }

    [GeneratedRegex(@"[\(（\[［]([^)）\]］]+)[\)）\]］]\s*$")]
    private static partial Regex BracketPattern();
}
