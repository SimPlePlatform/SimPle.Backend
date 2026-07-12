using System.Text.Json.Serialization;

namespace SimPle.Infrastructure.Capabilities;

/// <summary>Deserialization shape of the embedded capability.seed.v1.json manifest.</summary>
public sealed class CapabilitySeedManifest
{
    [JsonPropertyName("manifestVersion")]
    public string ManifestVersion { get; set; } = default!;

    [JsonPropertyName("profiles")]
    public List<CapabilitySeedProfileEntry> Profiles { get; set; } = new();
}

public sealed class CapabilitySeedProfileEntry
{
    [JsonPropertyName("gameSlug")]
    public string GameSlug { get; set; } = default!;

    [JsonPropertyName("capabilityVersion")]
    public int CapabilityVersion { get; set; }

    [JsonPropertyName("minPlayers")]
    public int MinPlayers { get; set; }

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("allowedModes")]
    public List<string> AllowedModes { get; set; } = new();

    [JsonPropertyName("timeControls")]
    public List<string> TimeControls { get; set; } = new();

    [JsonPropertyName("tieBreakRules")]
    public List<string> TieBreakRules { get; set; } = new();

    [JsonPropertyName("spectatorPolicies")]
    public List<string> SpectatorPolicies { get; set; } = new();

    [JsonPropertyName("ratedEligible")]
    public bool RatedEligible { get; set; }

    [JsonPropertyName("aiFillEligible")]
    public bool AiFillEligible { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
