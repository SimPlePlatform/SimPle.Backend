using SimPle.Domain.Common;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;

namespace SimPle.Domain.Capabilities;

/// <summary>
/// What a lobby or ticket may <em>configure</em> for a given game, at a pinned capability version (D2).
///
/// The brief assumes lobby settings come from "the pinned M4/M5 capability version", but Module 4's catalog has no
/// such thing — it models only slug, player bounds, a mode list, and a lifecycle counter, with nothing for time
/// controls, tie-break rules, spectator policy, or rated eligibility. Rather than mutate M4-owned schema and
/// re-seed a catalog it does not own, Module 6 owns this additive table keyed by
/// <c>(GameSlug, CapabilityVersion)</c>.
///
/// Ownership stays clean: M4 owns what a game <em>is</em>; M6 owns what a lobby may <em>configure</em>. If M9 adds
/// AI difficulty tiers or M10 adds real ratings, they extend this profile rather than M4's catalog.
///
/// A lobby/ticket pins <c>(GameSlug, CapabilityVersion)</c> at creation. Because the pin is immutable but the
/// profile can be deactivated underneath it, "capability disabled after create" becomes a real, testable path
/// rather than a theoretical one — see <see cref="Permits"/> and <see cref="ContradictsCatalog"/>.
/// </summary>
public class GameCapabilityProfile : Entity
{
    public string GameSlug { get; private set; } = default!;

    /// <summary>Immutable half of the pin. Bumped by publishing a new profile row, never by editing this one.</summary>
    public int CapabilityVersion { get; private set; }

    public int MinPlayers { get; private set; }
    public int MaxPlayers { get; private set; }

    // Npgsql maps List<string> to text[] natively, so these need no junction tables.
    public List<string> AllowedModes { get; private set; } = new();
    public List<string> TimeControls { get; private set; } = new();
    public List<string> TieBreakRules { get; private set; } = new();
    public List<string> SpectatorPolicies { get; private set; } = new();

    public bool RatedEligible { get; private set; }
    public bool AiFillEligible { get; private set; }

    /// <summary>
    /// A deactivated profile still exists (lobbies pinned to it must remain readable) but rejects every new
    /// command that depends on it.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Last manifest version that wrote this row (seeder bookkeeping only).</summary>
    public string ManifestVersion { get; private set; } = default!;

    private GameCapabilityProfile() { }

    public static GameCapabilityProfile Create(
        string gameSlug,
        int capabilityVersion,
        int minPlayers,
        int maxPlayers,
        IEnumerable<string> allowedModes,
        IEnumerable<string> timeControls,
        IEnumerable<string> tieBreakRules,
        IEnumerable<string> spectatorPolicies,
        bool ratedEligible,
        bool aiFillEligible,
        string manifestVersion)
    {
        var profile = new GameCapabilityProfile
        {
            GameSlug = RequireNonEmpty(gameSlug, nameof(gameSlug)),
            CapabilityVersion = capabilityVersion,
            MinPlayers = minPlayers,
            MaxPlayers = maxPlayers,
            AllowedModes = allowedModes.ToList(),
            TimeControls = timeControls.ToList(),
            TieBreakRules = tieBreakRules.ToList(),
            SpectatorPolicies = spectatorPolicies.ToList(),
            RatedEligible = ratedEligible,
            AiFillEligible = aiFillEligible,
            ManifestVersion = RequireNonEmpty(manifestVersion, nameof(manifestVersion)),
            IsActive = true,
        };

        profile.Validate();
        return profile;
    }

    /// <summary>
    /// Manifest re-application. <see cref="GameSlug"/> and <see cref="CapabilityVersion"/> are the pin and are never
    /// changed here — publishing different capabilities means publishing a <em>new version row</em>, because a
    /// lobby that pinned v1 must keep meaning what it meant when it was created.
    /// </summary>
    public void ApplyManifestUpdate(
        int minPlayers,
        int maxPlayers,
        IEnumerable<string> allowedModes,
        IEnumerable<string> timeControls,
        IEnumerable<string> tieBreakRules,
        IEnumerable<string> spectatorPolicies,
        bool ratedEligible,
        bool aiFillEligible,
        bool isActive,
        string manifestVersion)
    {
        MinPlayers = minPlayers;
        MaxPlayers = maxPlayers;
        AllowedModes = allowedModes.ToList();
        TimeControls = timeControls.ToList();
        TieBreakRules = tieBreakRules.ToList();
        SpectatorPolicies = spectatorPolicies.ToList();
        RatedEligible = ratedEligible;
        AiFillEligible = aiFillEligible;
        IsActive = isActive;
        ManifestVersion = RequireNonEmpty(manifestVersion, nameof(manifestVersion));

        Validate();
        Touch();
    }

    public void Deactivate()
    {
        if (!IsActive) return;   // idempotent
        IsActive = false;
        Touch();
    }

    // ── Validation used by the command layer ─────────────────────────────────

    /// <summary>
    /// Whether this profile permits the given lobby settings. Returns the specific reason on failure so the 6B
    /// service can distinguish an inactive pin from a merely unsupported combination.
    ///
    /// Callers must run this <em>before persistence</em> — that is what makes stale/unsupported combinations fail
    /// up front rather than producing a lobby nobody can start.
    /// </summary>
    public CapabilityCheck Permits(LobbySettings settings)
    {
        if (!IsActive)
            return CapabilityCheck.Fail("The pinned capability version is no longer active.");

        if (!string.Equals(settings.GameSlug, GameSlug, StringComparison.Ordinal))
            return CapabilityCheck.Fail("Settings name a different game than this capability profile.");

        if (settings.CapabilityVersion != CapabilityVersion)
            return CapabilityCheck.Fail("Settings pin a different capability version than this profile.");

        if (settings.MaxPlayers < MinPlayers || settings.MaxPlayers > MaxPlayers)
            return CapabilityCheck.Fail($"MaxPlayers must be between {MinPlayers} and {MaxPlayers} for this game.");

        if (!TimeControls.Contains(settings.TimeControlId, StringComparer.Ordinal))
            return CapabilityCheck.Fail($"Time control '{settings.TimeControlId}' is not supported by this game.");

        if (!TieBreakRules.Contains(settings.TieBreakRuleId, StringComparer.Ordinal))
            return CapabilityCheck.Fail($"Tie-break rule '{settings.TieBreakRuleId}' is not supported by this game.");

        if (!SpectatorPolicies.Contains(settings.SpectatorPolicy.ToString(), StringComparer.Ordinal))
            return CapabilityCheck.Fail($"Spectator policy '{settings.SpectatorPolicy}' is not supported by this game.");

        if (settings.Rated && !RatedEligible)
            return CapabilityCheck.Fail("This game does not support rated play.");

        if (settings.AiFillRequested && !AiFillEligible)
            return CapabilityCheck.Fail("This game does not support AI fill.");

        return CapabilityCheck.Pass();
    }

    /// <summary>
    /// Whether this profile permits the given <em>matchmaking ticket</em> (slice 6C).
    ///
    /// A ticket is not a lobby and cannot reuse <see cref="Permits(LobbySettings)"/>: it names a <c>Mode</c>, which
    /// <see cref="LobbySettings"/> has no field for, and it has no privacy, spectator policy, tie-break, or AI-fill
    /// to validate — a Quick Match ticket configures a search, not a room. Sharing one method would have meant
    /// inventing lobby-shaped values for a ticket and then checking them, which is how a validator starts passing
    /// things it never actually examined.
    ///
    /// <paramref name="playerCount"/> is the <em>exact</em> group size the queue will assemble, so it is checked
    /// against the closed interval — unlike a lobby's <c>MaxPlayers</c>, it is not merely a ceiling.
    /// </summary>
    public CapabilityCheck PermitsTicket(
        string gameSlug, int capabilityVersion, string mode, int playerCount, string timeControlId, bool rated)
    {
        if (!IsActive)
            return CapabilityCheck.Fail("The pinned capability version is no longer active.");

        if (!string.Equals(gameSlug, GameSlug, StringComparison.Ordinal))
            return CapabilityCheck.Fail("The ticket names a different game than this capability profile.");

        if (capabilityVersion != CapabilityVersion)
            return CapabilityCheck.Fail("The ticket pins a different capability version than this profile.");

        if (!AllowedModes.Contains(mode, StringComparer.Ordinal))
            return CapabilityCheck.Fail($"Mode '{mode}' is not supported by this game.");

        if (playerCount < MinPlayers || playerCount > MaxPlayers)
            return CapabilityCheck.Fail($"PlayerCount must be between {MinPlayers} and {MaxPlayers} for this game.");

        if (!TimeControls.Contains(timeControlId, StringComparer.Ordinal))
            return CapabilityCheck.Fail($"Time control '{timeControlId}' is not supported by this game.");

        if (rated && !RatedEligible)
            return CapabilityCheck.Fail("This game does not support rated play.");

        return CapabilityCheck.Pass();
    }

    /// <summary>
    /// Whether this profile has drifted from M4's catalog row for the same game. A profile that permits seat counts
    /// or modes the catalog does not is a data bug, not a user error: it would let a lobby be created that M5's
    /// engine cannot host. The command layer treats drift exactly like an inactive pin — fail closed, before
    /// persistence.
    ///
    /// Takes the two catalog facts it needs rather than the whole <see cref="Game"/> aggregate, so it stays a pure,
    /// unit-testable check.
    /// </summary>
    public CapabilityCheck ContradictsCatalog(int catalogMinPlayers, int catalogMaxPlayers, IEnumerable<string> catalogModes)
    {
        if (MinPlayers < catalogMinPlayers || MaxPlayers > catalogMaxPlayers)
        {
            return CapabilityCheck.Fail(
                $"Capability profile player bounds [{MinPlayers}, {MaxPlayers}] exceed the catalog's " +
                $"[{catalogMinPlayers}, {catalogMaxPlayers}].");
        }

        var catalog = catalogModes.ToHashSet(StringComparer.Ordinal);
        var extra = AllowedModes.FirstOrDefault(m => !catalog.Contains(m));
        if (extra is not null)
            return CapabilityCheck.Fail($"Capability profile allows mode '{extra}', which the catalog does not.");

        return CapabilityCheck.Pass();
    }

    private void Validate()
    {
        if (CapabilityVersion < 1)
            throw new ArgumentException("CapabilityVersion must be at least 1.", nameof(CapabilityVersion));
        if (MinPlayers < 2)
            throw new ArgumentException("MinPlayers must be at least 2 — a lobby is a multiplayer surface.", nameof(MinPlayers));
        if (MinPlayers > MaxPlayers)
            throw new ArgumentException("MinPlayers must be <= MaxPlayers.", nameof(MinPlayers));

        RequireNonEmptyAllowListed(AllowedModes, GameCatalogAllowLists.Modes, nameof(AllowedModes));
        RequireNonEmptyAllowListed(TimeControls, LobbyAllowLists.TimeControls, nameof(TimeControls));
        RequireNonEmptyAllowListed(TieBreakRules, LobbyAllowLists.TieBreakRules, nameof(TieBreakRules));

        if (SpectatorPolicies.Count == 0)
            throw new ArgumentException("SpectatorPolicies must contain at least one entry.", nameof(SpectatorPolicies));
        foreach (var policy in SpectatorPolicies)
        {
            if (!Enum.TryParse<SpectatorPolicy>(policy, ignoreCase: false, out _))
                throw new ArgumentException($"Spectator policy '{policy}' is not a valid SpectatorPolicy.", nameof(SpectatorPolicies));
        }
        RequireNoDuplicates(SpectatorPolicies, nameof(SpectatorPolicies));

        // A rated game must actually offer competitive play; "ranked" is M4's mode-allow-list spelling.
        if (RatedEligible && !AllowedModes.Contains("ranked", StringComparer.Ordinal))
            throw new ArgumentException("RatedEligible requires the 'ranked' mode to be allowed.", nameof(RatedEligible));

        if (AiFillEligible && !AllowedModes.Contains("ai", StringComparer.Ordinal))
            throw new ArgumentException("AiFillEligible requires the 'ai' mode to be allowed.", nameof(AiFillEligible));
    }

    private static void RequireNonEmptyAllowListed(List<string> values, IReadOnlySet<string> allowList, string paramName)
    {
        if (values.Count == 0)
            throw new ArgumentException($"{paramName} must contain at least one entry.", paramName);

        foreach (var value in values)
        {
            if (!allowList.Contains(value))
                throw new ArgumentException($"{paramName} value '{value}' is not in the allow-list.", paramName);
        }

        RequireNoDuplicates(values, paramName);
    }

    private static void RequireNoDuplicates(List<string> values, string paramName)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException($"{paramName} must not contain duplicates.", paramName);
    }

    private static string RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} must not be empty.", paramName);
        return value;
    }
}

/// <summary>Result of a capability check. The reason is safe to surface — it names a setting, never internals.</summary>
public sealed record CapabilityCheck(bool Allowed, string? Reason)
{
    public static CapabilityCheck Pass() => new(true, null);
    public static CapabilityCheck Fail(string reason) => new(false, reason);
}
