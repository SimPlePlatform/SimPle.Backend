namespace SimPle.Domain.Lobbies;

/// <summary>
/// Phase-1 platform allow-lists for lobby settings that Module 4's catalog does not model
/// (time controls, tie-break rules, regions). These are the <em>universe</em> of legal values; which subset a
/// given game actually permits is declared per-game by
/// <see cref="SimPle.Domain.Capabilities.GameCapabilityProfile"/> (D2). A value must clear both: it must be in
/// the platform allow-list here <em>and</em> in the pinned capability profile.
///
/// Mirrors <see cref="SimPle.Domain.Games.GameCatalogAllowLists"/> in shape and intent.
/// </summary>
public static class LobbyAllowLists
{
    public static readonly IReadOnlySet<string> TimeControls = new HashSet<string>(StringComparer.Ordinal)
    {
        "untimed", "bullet-1-0", "blitz-3-2", "blitz-5-0", "rapid-10-0", "classical-30-0",
    };

    public static readonly IReadOnlySet<string> TieBreakRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "sudden-death", "fastest-finish", "highest-score", "fewest-moves",
    };

    /// <summary>
    /// Phase 1 is same-region only: a ticket's region is part of its exact-match candidate pool key, so an
    /// unrecognized region would silently partition the queue. A lobby/ticket therefore never stores "Auto" —
    /// it stores a resolved member of this set (see <see cref="LobbyRegion.Resolve"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> Regions = new HashSet<string>(StringComparer.Ordinal)
    {
        "us-east", "us-west", "eu-west", "eu-central", "ap-south", "ap-southeast", "sa-east",
    };
}

/// <summary>Server-side region resolution. "Auto" is a request-time input only; it is never persisted.</summary>
public static class LobbyRegion
{
    /// <summary>The literal a client sends to ask the server to choose. Never stored.</summary>
    public const string Auto = "Auto";

    /// <summary>
    /// Resolves a requested region to an explicit, allow-listed region, in the brief's order:
    /// an explicit allow-listed request wins; otherwise the user's profile region if it is allow-listed;
    /// otherwise the deployment default.
    ///
    /// <paramref name="profileRegion"/> is deliberately treated as untrusted: <c>User.Region</c> is free text
    /// validated only for length (<c>UpdateProfileRequestValidator</c>), so a profile carrying "Narnia" must fall
    /// through to the default rather than partition the matchmaking queue into a pool of one.
    /// </summary>
    public static string Resolve(string? requestedRegion, string? profileRegion, string deploymentDefault)
    {
        if (!LobbyAllowLists.Regions.Contains(deploymentDefault))
        {
            throw new ArgumentException(
                $"Deployment default region '{deploymentDefault}' is not allow-listed.", nameof(deploymentDefault));
        }

        if (requestedRegion is not null
            && !string.Equals(requestedRegion, Auto, StringComparison.Ordinal)
            && LobbyAllowLists.Regions.Contains(requestedRegion))
        {
            return requestedRegion;
        }

        if (profileRegion is not null && LobbyAllowLists.Regions.Contains(profileRegion))
            return profileRegion;

        return deploymentDefault;
    }

    /// <summary>True when the value is an explicit, persistable region (never "Auto").</summary>
    public static bool IsResolved(string region) =>
        !string.Equals(region, Auto, StringComparison.Ordinal) && LobbyAllowLists.Regions.Contains(region);
}
