using SimPle.Domain.Games;

namespace SimPle.Domain.GameHost;

/// <summary>
/// The immutable identity and capability declaration of a game definition. The
/// <c>(Slug, EngineVersion)</c> pair is the registry key: it is chosen at compile time, never derived from
/// input, and never falls back to "latest" — an explicit version is mandatory so a replay of an old match
/// resolves the engine that actually produced it.
/// </summary>
public sealed class GameDefinitionMetadata
{
    public string Slug { get; }
    public int EngineVersion { get; }
    public int StateSchemaVersion { get; }
    public int MinPlayers { get; }
    public int MaxPlayers { get; }

    /// <summary>Modes this engine can host, from the shared catalog allow-list (<see cref="GameCatalogAllowLists.Modes"/>).</summary>
    public IReadOnlySet<string> SupportedModes { get; }

    /// <summary>The engine keeps state that at least one seat may not see (a hand, a hidden board).</summary>
    public bool HasHiddenInformation { get; }

    public bool SupportsSpectatorView { get; }
    public bool SupportsAi { get; }
    public bool SupportsTimer { get; }
    public bool SupportsRanked { get; }

    /// <summary>The engine guarantees byte-identical output for identical inputs. Required for golden vectors.</summary>
    public bool SupportsDeterministicReplay { get; }

    private GameDefinitionMetadata(
        string slug,
        int engineVersion,
        int stateSchemaVersion,
        int minPlayers,
        int maxPlayers,
        IReadOnlySet<string> supportedModes,
        bool hasHiddenInformation,
        bool supportsSpectatorView,
        bool supportsAi,
        bool supportsTimer,
        bool supportsRanked,
        bool supportsDeterministicReplay)
    {
        Slug = slug;
        EngineVersion = engineVersion;
        StateSchemaVersion = stateSchemaVersion;
        MinPlayers = minPlayers;
        MaxPlayers = maxPlayers;
        SupportedModes = supportedModes;
        HasHiddenInformation = hasHiddenInformation;
        SupportsSpectatorView = supportsSpectatorView;
        SupportsAi = supportsAi;
        SupportsTimer = supportsTimer;
        SupportsRanked = supportsRanked;
        SupportsDeterministicReplay = supportsDeterministicReplay;
    }

    public static GameDefinitionMetadata Create(
        string slug,
        int engineVersion,
        int stateSchemaVersion,
        int minPlayers,
        int maxPlayers,
        IEnumerable<string> supportedModes,
        bool hasHiddenInformation = false,
        bool supportsSpectatorView = false,
        bool supportsAi = false,
        bool supportsTimer = false,
        bool supportsRanked = false,
        bool supportsDeterministicReplay = true)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug must not be empty.", nameof(slug));
        if (engineVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(engineVersion), engineVersion, "EngineVersion must be positive.");
        if (stateSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(stateSchemaVersion), stateSchemaVersion, "StateSchemaVersion must be positive.");
        if (minPlayers < EngineLimits.MinPlayers)
            throw new ArgumentOutOfRangeException(nameof(minPlayers), minPlayers, $"MinPlayers must be at least {EngineLimits.MinPlayers}.");
        if (maxPlayers > EngineLimits.MaxPlayers)
            throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers, $"MaxPlayers must be at most {EngineLimits.MaxPlayers}.");
        if (minPlayers > maxPlayers)
            throw new ArgumentException("MinPlayers must be <= MaxPlayers.", nameof(minPlayers));

        var modes = new HashSet<string>(supportedModes, StringComparer.Ordinal);
        if (modes.Count == 0)
            throw new ArgumentException("At least one supported mode is required.", nameof(supportedModes));

        // Reuse the catalog's allow-list rather than a parallel copy: the engine and the Module 4 catalog row
        // are compared mode-for-mode by the compatibility validator, so they must speak the same vocabulary.
        foreach (var mode in modes)
        {
            if (!GameCatalogAllowLists.Modes.Contains(mode))
                throw new ArgumentException($"Mode '{mode}' is not in the catalog allow-list.", nameof(supportedModes));
        }

        if (supportsRanked && !modes.Contains("ranked"))
            throw new ArgumentException("SupportsRanked requires the 'ranked' mode to be declared.", nameof(supportsRanked));
        if (modes.Contains("ai") && !supportsAi)
            throw new ArgumentException("Declaring the 'ai' mode requires SupportsAi.", nameof(supportsAi));

        return new GameDefinitionMetadata(
            slug,
            engineVersion,
            stateSchemaVersion,
            minPlayers,
            maxPlayers,
            modes,
            hasHiddenInformation,
            supportsSpectatorView,
            supportsAi,
            supportsTimer,
            supportsRanked,
            supportsDeterministicReplay);
    }

    /// <summary>The registry key. Immutable; a duplicate fails application startup.</summary>
    public override string ToString() => $"{Slug}@v{EngineVersion}";
}
