using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SceneGallery.Plugin.BepisDb;

internal readonly record struct BepisDbArtworkData(
    string? UploaderName,
    string? UploaderId,
    string? Title,
    string? Description,
    IReadOnlyList<string> Tags);

internal static class BepisDbHtmlParser
{
    private static readonly HtmlParser Parser = new();

    public static BepisDbArtworkData? Parse(string html, Action<string> log)
    {
        try
        {
            var document = Parser.ParseDocument(html);

            var title = ExtractTitle(document);
            var (uploaderName, uploaderId) = ExtractUploader(document);
            var description = ExtractDescription(document);
            var tags = ExtractTags(document);

            if (title is null && uploaderName is null && tags.Count == 0)
            {
                log("HTML parser: no data found — page structure may have changed. Logging first 2000 chars of HTML for debugging.");
                log(html.Length > 2000 ? html[..2000] : html);
                return null;
            }

            return new BepisDbArtworkData(uploaderName, uploaderId, title, description, tags);
        }
        catch (Exception ex)
        {
            log($"HTML parse error: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractTitle(IDocument document)
    {
        // Try page title first (usually "CardName - BepisDB")
        var pageTitle = document.Title;
        if (pageTitle is not null)
        {
            var dashIndex = pageTitle.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dashIndex > 0)
                return pageTitle[..dashIndex].Trim();
            return pageTitle.Trim();
        }

        // Fallback: h1 or h2
        var heading = document.QuerySelector("h1, h2");
        return heading?.TextContent.Trim();
    }

    private static (string? Name, string? Id) ExtractUploader(IDocument document)
    {
        // Look for a user profile link (e.g., /user/view/1234)
        var userLink = document.QuerySelector("a[href*='/user/view/']");
        if (userLink is not null)
        {
            var name = userLink.TextContent.Trim();
            var href = userLink.GetAttribute("href") ?? "";
            var id = ExtractUserIdFromHref(href);
            return (name.Length > 0 ? name : null, id);
        }

        return (null, null);
    }

    private static string? ExtractUserIdFromHref(string href)
    {
        // Pattern: /user/view/1234 or https://db.bepis.moe/user/view/1234
        const string marker = "/user/view/";
        var idx = href.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = start;
        while (end < href.Length && char.IsDigit(href[end])) end++;
        return end > start ? href[start..end] : null;
    }

    private static string? ExtractDescription(IDocument document)
    {
        // Look for common description containers
        var desc = document.QuerySelector(".card-description, .description, [class*='desc']");
        return desc?.TextContent.Trim() is { Length: > 0 } text ? text : null;
    }

    private static IReadOnlyList<string> ExtractTags(IDocument document)
    {
        var tags = new List<string>();

        // Look for tag links or badge elements
        var tagElements = document.QuerySelectorAll("a[href*='tag='], .badge, .tag, [class*='tag']");
        foreach (var el in tagElements)
        {
            var text = el.TextContent.Trim();
            if (text.Length > 0 && !tags.Contains(text, StringComparer.OrdinalIgnoreCase))
                tags.Add(text);
        }

        return tags;
    }
}
