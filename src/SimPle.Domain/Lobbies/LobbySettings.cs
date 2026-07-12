namespace SimPle.Domain.Lobbies;

/// <summary>
/// The full mutable settings tuple of a lobby. Passed whole to <see cref="Lobby.ChangeSettings"/> so a partial
/// update can never leave the aggregate half-validated.
///
/// <paramref name="ResolvedRegion"/> is already resolved (see <see cref="LobbyRegion.Resolve"/>) — "Auto" never
/// reaches the domain.
/// </summary>
public sealed record LobbySettings(
    string GameSlug,
    int CapabilityVersion,
    LobbyPrivacy Privacy,
    int MaxPlayers,
    string TimeControlId,
    bool Rated,
    string ResolvedRegion,
    SpectatorPolicy SpectatorPolicy,
    string TieBreakRuleId,
    bool AiFillRequested)
{
    /// <summary>
    /// True when moving to <paramref name="next"/> changes something that alters <em>what match gets played</em>,
    /// and therefore must reset every joined non-host human's readiness (brief: "every match-affecting setting
    /// change ... resets readiness"; Risk #4).
    ///
    /// <see cref="Privacy"/> and <see cref="SpectatorPolicy"/> are deliberately excluded: they govern who may
    /// <em>see or reach</em> the lobby, not what is played, so toggling them cannot make a ready roster stale.
    /// Every other field — game, capability pin, seat count, time control, rated, region, tie-break, and AI fill —
    /// changes the match itself and does reset readiness.
    /// </summary>
    public bool IsMatchAffectingChangeTo(LobbySettings next) =>
        !string.Equals(GameSlug, next.GameSlug, StringComparison.Ordinal)
        || CapabilityVersion != next.CapabilityVersion
        || MaxPlayers != next.MaxPlayers
        || !string.Equals(TimeControlId, next.TimeControlId, StringComparison.Ordinal)
        || Rated != next.Rated
        || !string.Equals(ResolvedRegion, next.ResolvedRegion, StringComparison.Ordinal)
        || !string.Equals(TieBreakRuleId, next.TieBreakRuleId, StringComparison.Ordinal)
        || AiFillRequested != next.AiFillRequested;
}
