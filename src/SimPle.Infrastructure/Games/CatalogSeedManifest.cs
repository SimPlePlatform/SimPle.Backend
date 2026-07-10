using System.Text.Json.Serialization;

namespace SimPle.Infrastructure.Games;

/// <summary>Deserialization shape of the embedded catalog.seed.v1.json manifest.</summary>
public sealed class CatalogSeedManifest
{
    [JsonPropertyName("manifestVersion")]
    public string ManifestVersion { get; set; } = default!;

    [JsonPropertyName("games")]
    public List<CatalogSeedGameEntry> Games { get; set; } = new();
}

public sealed class CatalogSeedGameEntry
{
    [JsonPropertyName("legacyMockId")]
    public string LegacyMockId { get; set; } = default!;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = default!;

    [JsonPropertyName("rulesSummary")]
    public string RulesSummary { get; set; } = default!;

    [JsonPropertyName("category")]
    public string Category { get; set; } = default!;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = default!;

    [JsonPropertyName("estimatedDurationMinMinutes")]
    public int EstimatedDurationMinMinutes { get; set; }

    [JsonPropertyName("estimatedDurationMaxMinutes")]
    public int EstimatedDurationMaxMinutes { get; set; }

    [JsonPropertyName("minPlayers")]
    public int MinPlayers { get; set; }

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("modes")]
    public List<string> Modes { get; set; } = new();

    [JsonPropertyName("featuredRank")]
    public int? FeaturedRank { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("artToken")]
    public string ArtToken { get; set; } = default!;

    [JsonPropertyName("artColorA")]
    public string ArtColorA { get; set; } = default!;

    [JsonPropertyName("artColorB")]
    public string ArtColorB { get; set; } = default!;

    [JsonPropertyName("artAltText")]
    public string ArtAltText { get; set; } = default!;
}
