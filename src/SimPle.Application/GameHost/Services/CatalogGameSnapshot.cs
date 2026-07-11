namespace SimPle.Application.GameHost.Services;

/// <summary>
/// A minimal, immutable read of one Module 4 catalog row — <c>Slug</c>, player bounds, and mode capabilities —
/// used only by <see cref="ICatalogEngineCompatibilityValidator"/>.
/// <para>
/// This is deliberately not M4's <c>Game</c> aggregate (D1 in the reconciliation ledger). The validator core
/// stays free of a direct dependency on M4's private-setter, invariant-guarded entity, so mismatched fixtures
/// for the zero/matching/mismatched test matrix are trivial to construct. The real composition root maps
/// <c>Game.Slug</c>/<c>MinPlayers</c>/<c>MaxPlayers</c>/<c>Capabilities[].Mode</c> into this shape.
/// </para>
/// </summary>
public sealed class CatalogGameSnapshot
{
    public string Slug { get; }
    public int MinPlayers { get; }
    public int MaxPlayers { get; }
    public IReadOnlySet<string> Modes { get; }

    private CatalogGameSnapshot(string slug, int minPlayers, int maxPlayers, IReadOnlySet<string> modes)
    {
        Slug = slug;
        MinPlayers = minPlayers;
        MaxPlayers = maxPlayers;
        Modes = modes;
    }

    public static CatalogGameSnapshot Create(string slug, int minPlayers, int maxPlayers, IEnumerable<string> modes)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug must not be empty.", nameof(slug));
        if (minPlayers < 1)
            throw new ArgumentOutOfRangeException(nameof(minPlayers), minPlayers, "MinPlayers must be at least 1.");
        if (minPlayers > maxPlayers)
            throw new ArgumentException("MinPlayers must be <= MaxPlayers.", nameof(minPlayers));

        return new CatalogGameSnapshot(slug, minPlayers, maxPlayers, new HashSet<string>(modes, StringComparer.Ordinal));
    }
}

/// <summary>One drift signal between a registered engine's metadata and its catalog row's advertised shape.</summary>
public sealed class CatalogCompatibilityViolation
{
    public string Slug { get; }
    public string Reason { get; }

    private CatalogCompatibilityViolation(string slug, string reason)
    {
        Slug = slug;
        Reason = reason;
    }

    public static CatalogCompatibilityViolation Create(string slug, string reason) => new(slug, reason);

    public override string ToString() => $"{Slug}: {Reason}";
}
