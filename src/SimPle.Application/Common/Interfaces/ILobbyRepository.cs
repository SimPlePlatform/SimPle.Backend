using SimPle.Domain.Capabilities;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Data access for lobbies, invites, join credentials, and start requests.
///
/// Reads used inside a command are <em>tracked</em> — the command layer mutates the aggregate it read and saves it
/// in the same unit of work. Reads used to render a response are <c>AsNoTracking</c>.
///
/// Every write stages its caller-built <see cref="OutboxMessage"/> rows in the same <c>SaveChanges</c> as the
/// aggregate mutation, so a state change and its integration event commit or roll back together. There is no
/// method here that saves an event without its cause, or a cause without its event.
/// </summary>
public interface ILobbyRepository
{
    // ── Lobby reads ──────────────────────────────────────────────────────────

    /// <summary>Tracked, with the member collection loaded. The read half of a read-decide-write command.</summary>
    Task<Lobby?> GetForUpdateAsync(Guid lobbyId, CancellationToken ct = default);

    /// <summary>Untracked, with members. For rendering a response.</summary>
    Task<Lobby?> GetByIdAsync(Guid lobbyId, CancellationToken ct = default);

    /// <summary>
    /// Public discovery: <c>Open</c> + <c>Public</c> only, keyset-ordered on <c>(CreatedAt, Id)</c>. A private,
    /// expired, closed, or started lobby is not merely filtered out of the projection — it never enters the query,
    /// so it cannot affect page length or the cursor.
    /// </summary>
    Task<IReadOnlyList<Lobby>> GetPublicPageAsync(
        int limit, DateTime? afterCreatedAt, Guid? afterId, CancellationToken ct = default);

    /// <summary>The user's single joined nonterminal lobby, or null.</summary>
    Task<Lobby?> GetActiveLobbyForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The user's single nonterminal matchmaking ticket id, or null.
    ///
    /// Module 6B owns no ticket <em>commands</em> (those are 6C), but it must read this table: "one active lobby
    /// <strong>or</strong> one active ticket" is a cross-table invariant, and the two filtered unique indexes
    /// cannot see each other (brief Risk #2). The join/accept commands therefore check it inside the transaction
    /// that seats the member.
    /// </summary>
    Task<Guid?> GetActiveTicketIdForUserAsync(Guid userId, CancellationToken ct = default);

    // ── Credentials ──────────────────────────────────────────────────────────

    Task<LobbyJoinCredential?> GetActiveCredentialAsync(Guid lobbyId, CancellationToken ct = default);

    /// <summary>
    /// Resolves an active credential by its keyed digest. Returns null for a wrong, expired, rotated, revoked, or
    /// unknown value alike — the caller maps every one of them to the same <c>Lobbies.CredentialInvalid</c>, so
    /// the endpoint is not an oracle for which lobbies exist.
    /// </summary>
    Task<LobbyJoinCredential?> FindActiveByCodeDigestAsync(string codeDigest, CancellationToken ct = default);

    Task<LobbyJoinCredential?> FindActiveByLinkTokenDigestAsync(string linkTokenDigest, CancellationToken ct = default);

    // ── Invites ──────────────────────────────────────────────────────────────

    Task<LobbyInvite?> GetInviteForUpdateAsync(Guid inviteId, CancellationToken ct = default);

    Task<LobbyInvite?> GetPendingInviteAsync(Guid lobbyId, Guid inviteeUserId, CancellationToken ct = default);

    /// <summary>
    /// The invitee's pending, unexpired invites with the lobby and inviter needed to render them. The dashboard's
    /// "N active" badge is this same bounded query — a count is never shown without the list it summarizes.
    /// </summary>
    Task<IReadOnlyList<(LobbyInvite Invite, Lobby Lobby, User Inviter)>> GetPendingInvitesForUserAsync(
        Guid inviteeUserId, DateTime nowUtc, int limit, CancellationToken ct = default);

    // ── Expiry sweep (slice 6C) ──────────────────────────────────────────────

    /// <summary>
    /// Open/Starting lobbies past their 2-hour deadline, tracked, bounded, oldest first.
    ///
    /// A lobby is <em>swept</em> to <c>Expired</c> rather than merely being treated as expired on read. Both exist,
    /// and both are needed: the read-time <c>IsExpired</c> check is what makes an expired lobby immediately
    /// unusable even if the sweep is behind, while the sweep is what actually frees its members from the
    /// one-active-lobby invariant — a member row still pointing at a lobby whose state says <c>Open</c> would keep
    /// its owner locked out of joining anything else forever.
    /// </summary>
    Task<IReadOnlyList<Lobby>> GetExpiredLobbiesAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default);

    /// <summary>Pending invites past their 30-minute deadline, tracked, bounded, oldest first.</summary>
    Task<IReadOnlyList<LobbyInvite>> GetExpiredInvitesAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default);

    // ── Start requests ───────────────────────────────────────────────────────

    Task<LobbyStartRequest?> GetOpenStartRequestAsync(Guid lobbyId, int lobbyRevision, CancellationToken ct = default);

    Task<LobbyStartRequest?> GetStartRequestByIdempotencyKeyAsync(
        Guid lobbyId, string idempotencyKey, CancellationToken ct = default);

    // ── Cross-module reads (M3 blocks, M4 catalog, M6 capability profiles) ───

    Task<GameCapabilityProfile?> GetCapabilityProfileAsync(
        string gameSlug, int capabilityVersion, CancellationToken ct = default);

    /// <summary>
    /// The highest-versioned <c>IsActive</c> profile for a slug — the one a new lobby/ticket should pin. Not for
    /// re-validating an existing lobby, which pins an exact <c>(GameSlug, CapabilityVersion)</c> instead.
    /// </summary>
    Task<GameCapabilityProfile?> GetActiveCapabilityProfileAsync(
        string gameSlug, CancellationToken ct = default);

    Task<Game?> GetGameAsync(string gameSlug, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetGameModesAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>
    /// The subset of <paramref name="candidateUserIds"/> that have a block with <paramref name="userId"/> in
    /// <em>either</em> direction. Returns the offenders rather than a bool so the caller can distinguish "you
    /// blocked the host" from "a member blocked you" when it matters, and so one round trip covers a whole roster.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetBlockedCounterpartsAsync(
        Guid userId, IReadOnlyList<Guid> candidateUserIds, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, User>> GetUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    Task<bool> AreFriendsAsync(Guid userA, Guid userB, CancellationToken ct = default);

    // ── Writes (each stages its outbox rows in the same SaveChanges) ─────────

    Task AddLobbyAsync(
        Lobby lobby, LobbyJoinCredential credential, IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default);

    Task AddInviteAsync(LobbyInvite invite, IReadOnlyList<OutboxMessage> events, CancellationToken ct = default);

    Task AddStartRequestAsync(
        LobbyStartRequest request, IReadOnlyList<OutboxMessage> events, CancellationToken ct = default);

    /// <summary>Rotation: the outgoing credential is already <c>MarkRotated</c>-ed by the caller.</summary>
    Task RotateCredentialAsync(
        LobbyJoinCredential outgoing, LobbyJoinCredential incoming, IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default);

    /// <summary>
    /// Persists whatever the command mutated on already-tracked aggregates, plus its events.
    ///
    /// This is the call that can throw — a unique-index violation (23505) from a racing writer, or a row-version
    /// (xmin) mismatch from a lost update. Both are deliberately allowed to propagate: catching them here would
    /// mean deciding the outcome without re-reading, which is exactly the bug R3 exists to prevent. The bounded
    /// retry above the command re-runs the whole read-decide-write instead.
    /// </summary>
    Task SaveAsync(IReadOnlyList<OutboxMessage> events, CancellationToken ct = default);
}
