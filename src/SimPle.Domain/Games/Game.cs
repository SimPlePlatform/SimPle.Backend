using SimPle.Domain.Common;

namespace SimPle.Domain.Games;

/// <summary>
/// Catalog entry for a game. The actual game logic lives in the GameHost module. This slice (4A) owns only
/// the domain/schema shape; the read API, search/cursor, favorites endpoints and outbox emission are 4B.
/// </summary>
public class Game : Entity
{
    private readonly List<GameTag> _tags = new();
    private readonly List<GameModeCapability> _capabilities = new();

    public string Slug { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Summary { get; private set; } = default!;
    public string RulesSummary { get; private set; } = default!;
    public string Category { get; private set; } = default!;
    public GameDifficulty Difficulty { get; private set; }
    public int EstimatedDurationMinMinutes { get; private set; }
    public int EstimatedDurationMaxMinutes { get; private set; }
    public int MinPlayers { get; private set; }
    public int MaxPlayers { get; private set; }
    public GameLifecycle Lifecycle { get; private set; }

    /// <summary>
    /// Monotonic per-game counter bumped on every lifecycle transition. Starts at 1 (the as-created state);
    /// used as the outbox <c>AggregateDomainVersion</c> for <c>GameLifecycleChangedV1</c> so a retried
    /// transition is idempotent while two distinct transitions never collide, even across an
    /// Available &lt;-&gt; Maintenance cycle. Mirrors <see cref="SimPle.Domain.Friends.Friendship.DomainVersion"/>.
    /// </summary>
    public int LifecycleVersion { get; private set; } = 1;

    public int? FeaturedRank { get; private set; }
    public int SortOrder { get; private set; }
    public string ArtToken { get; private set; } = default!;
    public string ArtColorA { get; private set; } = default!;
    public string ArtColorB { get; private set; } = default!;
    public string ArtAltText { get; private set; } = default!;

    /// <summary>Last manifest version that wrote this row (seeder bookkeeping only).</summary>
    public string ManifestVersion { get; private set; } = default!;

    public uint Version { get; private set; }   // mapped to xmin via IsRowVersion() in EF config

    public IReadOnlyList<GameTag> Tags => _tags;
    public IReadOnlyList<GameModeCapability> Capabilities => _capabilities;

    private Game() { }

    public static Game Create(
        string slug,
        string name,
        string summary,
        string rulesSummary,
        GameDifficulty difficulty,
        int estimatedDurationMinMinutes,
        int estimatedDurationMaxMinutes,
        int minPlayers,
        int maxPlayers,
        GameLifecycle initialLifecycle,
        int? featuredRank,
        int sortOrder,
        string artToken,
        string artColorA,
        string artColorB,
        string artAltText,
        string manifestVersion,
        string category,
        IEnumerable<string> tags,
        IEnumerable<string> modes)
    {
        var game = new Game
        {
            Slug = RequireNonEmpty(slug, nameof(slug)),
            Name = RequireNonEmpty(name, nameof(name)),
            Summary = RequireNonEmpty(summary, nameof(summary)),
            RulesSummary = RequireNonEmpty(rulesSummary, nameof(rulesSummary)),
            Category = RequireValidCategory(category),
            Difficulty = difficulty,
            EstimatedDurationMinMinutes = estimatedDurationMinMinutes,
            EstimatedDurationMaxMinutes = estimatedDurationMaxMinutes,
            MinPlayers = minPlayers,
            MaxPlayers = maxPlayers,
            Lifecycle = initialLifecycle,
            FeaturedRank = featuredRank,
            SortOrder = sortOrder,
            ArtToken = RequireNonEmpty(artToken, nameof(artToken)),
            ArtColorA = RequireNonEmpty(artColorA, nameof(artColorA)),
            ArtColorB = RequireNonEmpty(artColorB, nameof(artColorB)),
            ArtAltText = RequireNonEmpty(artAltText, nameof(artAltText)),
            ManifestVersion = RequireNonEmpty(manifestVersion, nameof(manifestVersion)),
        };

        game.Validate();
        game.ReplaceTags(tags);
        game.ReplaceModes(modes);
        game.ValidateModesImplyMultiplayer();

        return game;
    }

    /// <summary>
    /// Manifest upgrade path used by the seeder: Lifecycle and Slug are never changed here. Re-validates all
    /// invariants and replaces the tag/mode child collections wholesale.
    /// </summary>
    public void ApplyManifestUpdate(
        string name,
        string summary,
        string rulesSummary,
        GameDifficulty difficulty,
        int estimatedDurationMinMinutes,
        int estimatedDurationMaxMinutes,
        int minPlayers,
        int maxPlayers,
        int? featuredRank,
        int sortOrder,
        string artToken,
        string artColorA,
        string artColorB,
        string artAltText,
        string manifestVersion,
        string category,
        IEnumerable<string> tags,
        IEnumerable<string> modes)
    {
        Name = RequireNonEmpty(name, nameof(name));
        Summary = RequireNonEmpty(summary, nameof(summary));
        RulesSummary = RequireNonEmpty(rulesSummary, nameof(rulesSummary));
        Category = RequireValidCategory(category);
        Difficulty = difficulty;
        EstimatedDurationMinMinutes = estimatedDurationMinMinutes;
        EstimatedDurationMaxMinutes = estimatedDurationMaxMinutes;
        MinPlayers = minPlayers;
        MaxPlayers = maxPlayers;
        FeaturedRank = featuredRank;
        SortOrder = sortOrder;
        ArtToken = RequireNonEmpty(artToken, nameof(artToken));
        ArtColorA = RequireNonEmpty(artColorA, nameof(artColorA));
        ArtColorB = RequireNonEmpty(artColorB, nameof(artColorB));
        ArtAltText = RequireNonEmpty(artAltText, nameof(artAltText));
        ManifestVersion = RequireNonEmpty(manifestVersion, nameof(manifestVersion));

        Validate();
        ReplaceTags(tags);
        ReplaceModes(modes);
        ValidateModesImplyMultiplayer();

        Touch();
    }

    // ── Lifecycle transitions ───────────────────────────────────────────────

    public void Publish()
    {
        if (Lifecycle != GameLifecycle.Draft)
            throw new InvalidOperationException($"Cannot publish (Draft -> ComingSoon) from {Lifecycle}.");
        Lifecycle = GameLifecycle.ComingSoon;
        LifecycleVersion += 1;
        Touch();
    }

    public void MakeAvailable()
    {
        if (Lifecycle != GameLifecycle.ComingSoon && Lifecycle != GameLifecycle.Maintenance)
            throw new InvalidOperationException($"Cannot transition to Available from {Lifecycle}.");
        Lifecycle = GameLifecycle.Available;
        LifecycleVersion += 1;
        Touch();
    }

    public void EnterMaintenance()
    {
        if (Lifecycle != GameLifecycle.Available)
            throw new InvalidOperationException($"Cannot transition to Maintenance from {Lifecycle}.");
        Lifecycle = GameLifecycle.Maintenance;
        LifecycleVersion += 1;
        Touch();
    }

    public void Retire()
    {
        if (Lifecycle == GameLifecycle.Retired)
            throw new InvalidOperationException("Retired is terminal and cannot be re-retired.");
        Lifecycle = GameLifecycle.Retired;
        FeaturedRank = null;
        LifecycleVersion += 1;
        Touch();
    }

    /// <summary>Generic entry point enforcing the exact allowed-edges table; delegates to the named methods.</summary>
    public void TransitionTo(GameLifecycle target)
    {
        switch (target)
        {
            case GameLifecycle.ComingSoon:
                Publish();
                break;
            case GameLifecycle.Available:
                MakeAvailable();
                break;
            case GameLifecycle.Maintenance:
                EnterMaintenance();
                break;
            case GameLifecycle.Retired:
                Retire();
                break;
            default:
                throw new InvalidOperationException($"Cannot transition to {target}.");
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void Validate()
    {
        if (MinPlayers < 1)
            throw new ArgumentException("MinPlayers must be at least 1.", nameof(MinPlayers));
        if (MinPlayers > MaxPlayers)
            throw new ArgumentException("MinPlayers must be <= MaxPlayers.", nameof(MinPlayers));
        if (EstimatedDurationMinMinutes > EstimatedDurationMaxMinutes)
            throw new ArgumentException(
                "EstimatedDurationMinMinutes must be <= EstimatedDurationMaxMinutes.",
                nameof(EstimatedDurationMinMinutes));
        if (FeaturedRank is not null && (Lifecycle == GameLifecycle.Draft || Lifecycle == GameLifecycle.Retired))
            throw new ArgumentException("FeaturedRank may only be set when Lifecycle is not Draft or Retired.");
    }

    private void ReplaceTags(IEnumerable<string> tags)
    {
        var values = tags.ToList();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Tag values must not contain duplicates.", nameof(tags));
        foreach (var value in values)
        {
            if (!GameCatalogAllowLists.Tags.Contains(value))
                throw new ArgumentException($"Tag '{value}' is not in the allow-list.", nameof(tags));
            if (value == Category)
                throw new ArgumentException($"Tag '{value}' duplicates the category and must not be repeated.", nameof(tags));
        }

        _tags.Clear();
        foreach (var value in values)
            _tags.Add(GameTag.Create(Id, value));
    }

    private static string RequireValidCategory(string category)
    {
        RequireNonEmpty(category, nameof(category));
        if (!GameCatalogAllowLists.Tags.Contains(category))
            throw new ArgumentException($"Category '{category}' is not in the allow-list.", nameof(category));
        return category;
    }

    private void ReplaceModes(IEnumerable<string> modes)
    {
        var values = modes.ToList();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Mode values must not contain duplicates.", nameof(modes));
        foreach (var value in values)
        {
            if (!GameCatalogAllowLists.Modes.Contains(value))
                throw new ArgumentException($"Mode '{value}' is not in the allow-list.", nameof(modes));
        }

        _capabilities.Clear();
        foreach (var value in values)
            _capabilities.Add(GameModeCapability.Create(Id, value));
    }

    private void ValidateModesImplyMultiplayer()
    {
        var modeValues = _capabilities.Select(c => c.Mode).ToHashSet(StringComparer.Ordinal);
        if (modeValues.Contains("ranked") && !modeValues.Contains("multiplayer"))
            throw new ArgumentException("Mode 'ranked' requires 'multiplayer' to also be present.");
    }

    private static string RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} must not be empty.", paramName);
        return value;
    }
}

public enum GameDifficulty { Easy, Medium, Hard }

public enum GameLifecycle { Draft, ComingSoon, Available, Maintenance, Retired }

/// <summary>
/// Phase-1 allow-lists for game tags and mode capabilities. Used by domain validation on <see cref="Game"/>
/// creation/update and by the seeder/manifest validator before any database write.
/// </summary>
public static class GameCatalogAllowLists
{
    public static readonly IReadOnlySet<string> Tags = new HashSet<string>(StringComparer.Ordinal)
    {
        "puzzle", "logic", "arcade", "reaction", "strategy", "classic", "vocabulary", "memory",
    };

    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal)
    {
        "solo", "cooperative", "multiplayer", "ai", "ranked", "quick-match",
    };
}
