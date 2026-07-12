using SimPle.Application.Lobbies.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.Lobbies.Services;

/// <summary>
/// The lobby command surface (slice 6B). Matchmaking tickets, the matching/expiry workers, and the outbox
/// dispatcher are slice 6C and are not here.
///
/// Every method takes the actor as its first argument and it is always the JWT <c>sub</c> claim — no method
/// accepts a caller-supplied identity. Authorization is re-evaluated against the object's current membership and
/// host role on every call, never against a prior step's result.
/// </summary>
public interface ILobbiesService
{
    Task<Result<CreateLobbyResultDto>> CreateAsync(
        Guid actorUserId, CreateLobbyRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// The active capability profile (D2) for a game slug — the pinned-version source a client reads before
    /// calling <see cref="CreateAsync"/> or a matchmaking ticket create, so it never has to guess a
    /// <c>capabilityVersion</c> or an allowed time control/tie-break rule/spectator policy.
    /// </summary>
    Task<Result<GameCapabilityProfileDto>> GetCapabilityProfileAsync(
        string gameSlug, CancellationToken ct = default);

    /// <summary>Member or authorized viewer. A foreign/private/missing lobby is one indistinguishable 404.</summary>
    Task<Result<LobbyDto>> GetAsync(Guid actorUserId, Guid lobbyId, CancellationToken ct = default);

    /// <summary>Bounded public discovery. Private lobbies never appear and never affect totals or cursors.</summary>
    Task<Result<CursorPage<LobbySummaryDto>>> GetPublicAsync(
        Guid actorUserId, int limit, string? cursor, CancellationToken ct = default);

    /// <summary>
    /// Join a lobby. Code/link token (secrets, carried in the body — never a path segment) or a lobbyId (a resource
    /// identifier, only honored when that lobby is currently Public and Open).
    /// </summary>
    Task<Result<LobbyDto>> JoinByCredentialAsync(
        Guid actorUserId, JoinLobbyRequestDto request, CancellationToken ct = default);

    Task<Result> LeaveAsync(Guid actorUserId, Guid lobbyId, CancellationToken ct = default);

    Task<Result<LobbyDto>> SetReadinessAsync(
        Guid actorUserId, Guid lobbyId, SetReadinessRequestDto request, CancellationToken ct = default);

    Task<Result<LobbyDto>> UpdateSettingsAsync(
        Guid actorUserId, Guid lobbyId, UpdateLobbySettingsRequestDto request, CancellationToken ct = default);

    Task<Result<LobbyDto>> KickAsync(
        Guid actorUserId, Guid lobbyId, KickMemberRequestDto request, CancellationToken ct = default);

    Task<Result<LobbyCredentialDto>> RotateCredentialAsync(
        Guid actorUserId, Guid lobbyId, CancellationToken ct = default);

    Task<Result<LobbyInviteDto>> CreateInviteAsync(
        Guid actorUserId, Guid lobbyId, CreateInviteRequestDto request, CancellationToken ct = default);

    Task<Result> RevokeInviteAsync(
        Guid actorUserId, Guid lobbyId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// Redeem a targeted invite (<strong>R6</strong> — a route the approved spec's table omits).
    ///
    /// Without it an invitee has no way to reach a seat: join takes a credential, and handing every invitee the
    /// lobby's private code would both leak it and make a revoked invite still redeemable. Accept runs the same
    /// bounded transaction as a credential join — block re-check, one-active-lobby-or-ticket, capacity, readiness
    /// reset — so an invited member is seated under exactly the same invariants as any other.
    /// </summary>
    Task<Result<LobbyDto>> AcceptInviteAsync(Guid actorUserId, Guid inviteId, CancellationToken ct = default);

    Task<Result> DeclineInviteAsync(Guid actorUserId, Guid inviteId, CancellationToken ct = default);

    /// <summary>Returns <c>Lobbies.MatchRuntimeUnavailable</c> while no M8 consumer is registered.</summary>
    Task<Result<StartLobbyResultDto>> StartAsync(
        Guid actorUserId, Guid lobbyId, StartLobbyRequestDto request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<LobbyInviteDto>>> GetMyInvitesAsync(
        Guid actorUserId, CancellationToken ct = default);

    /// <summary>The dashboard's active lobby <em>or</em> ticket. Never both — that is the invariant.</summary>
    Task<Result<ActiveContextDto>> GetMyActiveAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Rematch from a terminal match. M8 owns matches and does not exist, so there is no terminal match to read
    /// and this honestly returns <c>Lobbies.MatchRuntimeUnavailable</c> rather than fabricating a lobby from
    /// settings nobody recorded.
    /// </summary>
    Task<Result<CreateLobbyResultDto>> CreateRematchLobbyAsync(
        Guid actorUserId, Guid terminalMatchId, CancellationToken ct = default);
}

/// <summary>
/// Module 6's error catalogue, in the codebase's PascalCase dot-namespaced house style
/// (reconciliation <strong>R1</strong> — the brief writes them <c>lobby.snake_case</c>, but every existing
/// consumer, including the frontend's <c>switch (error.code)</c> mappings, reads <c>Games.NotFound</c> /
/// <c>Friends.RequestCooldown</c>. Identical semantics; one casing scheme.)
/// </summary>
public static class LobbyErrors
{
    /// <summary>
    /// <strong>Privacy-safe.</strong> Deliberately returned for missing, expired-and-swept, private-and-
    /// unauthorized, and another user's lobby <em>alike</em>. A 403 here would confirm the id exists, which is
    /// exactly the BOLA leak (OWASP API1:2023) the 404 exists to close.
    /// </summary>
    public const string NotFound = "Lobbies.NotFound";

    public const string Full = "Lobbies.Full";
    public const string Closed = "Lobbies.Closed";
    public const string Expired = "Lobbies.Expired";
    public const string Blocked = "Lobbies.Blocked";
    public const string StaleRevision = "Lobbies.StaleRevision";

    /// <summary>Actor <em>is</em> a member but is not the host. Safe to disclose — they can already see the lobby.</summary>
    public const string Forbidden = "Lobbies.Forbidden";

    public const string AlreadyActive = "Lobbies.AlreadyActive";
    public const string CapabilityDisabled = "Lobbies.CapabilityDisabled";

    /// <summary>No active capability profile exists for the requested slug. Not privacy-sensitive — game slugs are
    /// public catalog data — so this is a plain 404 naming the actual code, unlike <see cref="NotFound"/>.</summary>
    public const string CapabilityNotFound = "Lobbies.CapabilityNotFound";

    /// <summary>M8 is not registered. Start, rematch, and queue execution are honestly unavailable.</summary>
    public const string MatchRuntimeUnavailable = "Lobbies.MatchRuntimeUnavailable";

    /// <summary>
    /// <strong>Deliberately indistinguishable</strong> across wrong / expired / rotated / revoked / closed-lobby.
    /// Any finer-grained answer would turn the join endpoint into an oracle for which codes are live.
    /// </summary>
    public const string CredentialInvalid = "Lobbies.CredentialInvalid";

    /// <summary>The bounded retry budget was spent. A typed 409 — never a 500 (brief Risk #5).</summary>
    public const string ConcurrencyConflict = "Lobbies.ConcurrencyConflict";

    public const string NotStartable = "Lobbies.NotStartable";
    public const string InvalidTarget = "Lobbies.InvalidTarget";
    public const string ValidationFailed = "Validation.Failed";
    public const string InvalidCursor = "Pagination.InvalidCursor";
    public const string RateLimitExceeded = "RateLimit.Exceeded";
}
