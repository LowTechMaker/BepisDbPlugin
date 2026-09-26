using System.Text.Json;

namespace SceneGallery.Plugin.BepisDb;

/// <summary>Pure provider response parsing. HTTP retry, authentication and cache policy stay with their owners.</summary>
internal static class BepisDbResponseParser
{
    internal static BepisDbCardParseResult ParseCard(string json)
    {
        BepisDbApiResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BepisDbApiResponse>(json);
        }
        catch (JsonException ex)
        {
            return new(null, $"invalid JSON: {ex.Message}");
        }
        if (response?.Type != "success" || response.Data?.Card is null)
            return new(null, response?.Error ?? "no card data");
        var card = response.Data.Card;
        return card.Id <= 0 || string.IsNullOrWhiteSpace(card.CardType)
            ? new(null, "card is missing id or cardType")
            : new(card, null);
    }

    internal static bool IsCloudflareChallenge(string body)
        => body.Contains("cf_chl", StringComparison.OrdinalIgnoreCase)
           || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
           || body.Contains("Enable JavaScript and cookies", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct BepisDbCardParseResult(BepisDbCardData? Card, string? SchemaError);
