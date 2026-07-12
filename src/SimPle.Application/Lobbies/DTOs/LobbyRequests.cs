namespace SimPle.Application.Lobbies.DTOs;

/// <summary>
/// Lobby creation. <paramref name="Region"/> may be <c>"Auto"</c> or omitted — the server resolves it and stores
/// an explicit region; "Auto" is never persisted.
///
/// There is deliberately no <c>hostUserId</c>: the actor is always the JWT <c>sub</c> claim. A body-supplied
/// identity is the classic BOLA vector and is not accepted anywhere in this module.
/// </summary>
public sealed record CreateLobbyRequestDto(
    string GameSlug,
    int CapabilityVersion,
    string Privacy,
    int MaxPlayers,
    string TimeControlId,
    bool Rated,
    string? Region,
    string SpectatorPolicy,
    string TieBreakRuleId,
    bool AiFillRequested);

/// <summary>
/// Join a lobby. Exactly one of <paramref name="Code"/> / <paramref name="LinkToken"/> / <paramref name="LobbyId"/>
/// is supplied.
///
/// The credential travels in the <em>body</em>, never in the path: a credential is a secret, not a resource
/// identifier, and a path segment lands in access logs, referrers, and browser history.
///
/// <paramref name="LobbyId"/> is a resource id, not a secret — it is only ever honored when the target lobby is
/// currently <c>Public</c> and <c>Open</c> (the same visibility rule as the public browse listing and single-lobby
/// read), so naming a private or foreign lobby's id here answers with the same privacy-safe not-found as everywhere
/// else in this module. It carries no join throttle: unlike a guessed code, it identifies a specific already-public
/// row rather than searching a credential space.
/// </summary>
public sealed record JoinLobbyRequestDto(
    string? Code,
    string? LinkToken,
    Guid? LobbyId = null);

/// <summary>
/// Host settings change. Sends the whole settings tuple, so a partial update can never leave the lobby
/// half-validated. <paramref name="ExpectedRevision"/> is the optimistic-concurrency token: a mismatch is a typed
/// <c>Lobbies.StaleRevision</c> carrying the current state, never a 500 and never a silent overwrite.
/// </summary>
public sealed record UpdateLobbySettingsRequestDto(
    string GameSlug,
    int CapabilityVersion,
    string Privacy,
    int MaxPlayers,
    string TimeControlId,
    bool Rated,
    string? Region,
    string SpectatorPolicy,
    string TieBreakRuleId,
    bool AiFillRequested,
    int ExpectedRevision);

public sealed record SetReadinessRequestDto(
    bool IsReady,
    int ExpectedRevision);

public sealed record KickMemberRequestDto(
    Guid TargetUserId,
    int ExpectedRevision);

public sealed record CreateInviteRequestDto(
    Guid InviteeUserId);

/// <summary>
/// Start. <paramref name="IdempotencyKey"/> makes a client retry replay the existing request rather than mint a
/// second one — the partial unique index on <c>(LobbyId, LobbyRevision) WHERE State = 'Open'</c> is what actually
/// enforces that; the key is how the caller recognizes its own prior attempt.
/// </summary>
public sealed record StartLobbyRequestDto(
    int ExpectedRevision,
    string IdempotencyKey);
