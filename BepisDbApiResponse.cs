using System.Text.Json.Serialization;

namespace SceneGallery.Plugin.BepisDb;

internal sealed class BepisDbApiResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("data")]
    public BepisDbApiData? Data { get; set; }
}

internal sealed class BepisDbApiData
{
    [JsonPropertyName("card")]
    public BepisDbCardData? Card { get; set; }
}

internal sealed class BepisDbCardData
{
    [JsonPropertyName("cardType")]
    public string? CardType { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("customName")]
    public string? CustomName { get; set; }

    [JsonPropertyName("uploader")]
    public BepisDbUploader? Uploader { get; set; }

    [JsonPropertyName("tags")]
    public List<BepisDbTag>? Tags { get; set; }

    [JsonPropertyName("downloadCount")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("votes")]
    public int Votes { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("dateCreatedUtc")]
    public string? DateCreatedUtc { get; set; }

    [JsonPropertyName("isFeatured")]
    public bool IsFeatured { get; set; }

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; set; }

    [JsonPropertyName("obsoletedBy")]
    public long? ObsoletedBy { get; set; }

    [JsonPropertyName("cardData")]
    public BepisDbCardMetadata? CardData { get; set; }
}

internal sealed class BepisDbUploader
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("vanityUrl")]
    public string? VanityUrl { get; set; }
}

internal sealed class BepisDbTag
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

internal sealed class BepisDbCardMetadata
{
    [JsonPropertyName("maleCount")]
    public int MaleCount { get; set; }

    [JsonPropertyName("femaleCount")]
    public int FemaleCount { get; set; }

    [JsonPropertyName("hasTimeline")]
    public bool HasTimeline { get; set; }

    [JsonPropertyName("objectCount")]
    public int ObjectCount { get; set; }

    [JsonPropertyName("hasExtendedInfo")]
    public bool HasExtendedInfo { get; set; }
}
