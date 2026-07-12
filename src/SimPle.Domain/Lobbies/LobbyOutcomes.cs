namespace SimPle.Domain.Lobbies;

/// <summary>
/// Expected domain outcomes of a lobby mutation. These are <em>results</em>, not exceptions: "the lobby is full"
/// and "you are not the host" are ordinary, testable states that map 1:1 onto the module's error catalogue, so
/// modelling them as control flow keeps the 6B service layer free of exception-driven branching.
///
/// Exceptions remain reserved for programmer error (a malformed setting, an out-of-allow-list value).
/// </summary>
public enum LobbyOutcome
{
    /// <summary>The mutation was applied and <see cref="Lobby.Revision"/> was bumped.</summary>
    Ok,

    /// <summary>Lobby is Started/Closed/Expired — terminal states reject every mutation. → <c>Lobbies.Closed</c></summary>
    Closed,

    /// <summary>Past <see cref="Lobby.ExpiresAtUtc"/>. → <c>Lobbies.Expired</c></summary>
    Expired,

    /// <summary>Capacity reached; the last-seat loser lands here. → <c>Lobbies.Full</c></summary>
    Full,

    /// <summary>Actor is a member but not the host, for a host-only action. → <c>Lobbies.Forbidden</c></summary>
    Forbidden,

    /// <summary>Actor holds no joined membership in this lobby. → privacy-safe <c>Lobbies.NotFound</c></summary>
    NotMember,

    /// <summary>Actor already holds a joined seat here.</summary>
    AlreadyJoined,

    /// <summary>Target is not joined, or is the actor where self-targeting is illegal (host cannot kick self).</summary>
    InvalidTarget,

    /// <summary>Lobby is not in a state from which a start may begin.</summary>
    NotStartable,
}

/// <summary>
/// A leave is the one mutation with a structural side effect: it may transfer the host or close the lobby.
/// Callers need all three facts, so they are returned together rather than re-derived by re-reading the aggregate.
/// </summary>
/// <param name="Outcome">Whether the leave applied.</param>
/// <param name="NewHostUserId">Set when hosting transferred to another member.</param>
/// <param name="ClosedReason">Set when the leave closed the lobby (no eligible successor).</param>
public sealed record LobbyLeaveResult(
    LobbyOutcome Outcome,
    Guid? NewHostUserId,
    LobbyClosedReason? ClosedReason)
{
    public static LobbyLeaveResult Failed(LobbyOutcome outcome) => new(outcome, null, null);
}
