using System.Text.RegularExpressions;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb;

internal static partial class BepisDbAuthorFolderNameParser
{
    [GeneratedRegex(@"^(?<name>.+?)\s*[\(（\[]\s*(?<id>\d{1,12})\s*[\)）\]]$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedForm();

    public static ParsedAuthor? TryParse(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        var match = BracketedForm().Match(folderName);
        if (!match.Success) return null;

        var id = match.Groups["id"].Value.TrimStart('0');
        if (id.Length == 0) id = "0";

        var name = match.Groups["name"].Value.Trim();
        if (name.Length == 0) name = id;

        return new ParsedAuthor(new AuthorKey(BepisDbFilenameParser.ProviderId, id), name);
    }
}
