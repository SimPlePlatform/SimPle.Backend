using Microsoft.EntityFrameworkCore;
using SimPle.Application.Common.Interfaces;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Friends;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Repositories;

/// <summary>
/// Lobby data access.
///
/// Nothing here catches a unique-violation or row-version exception. That is deliberate and is the whole point of
/// R3: a repository that swallowed <c>23505</c> would have to decide the outcome without re-reading, which is the
/// bug the bounded whole-command retry exists to prevent. Contention is allowed to propagate to
/// <see cref="LobbyCommandRunner"/>, which reruns the command so it can re-read and answer truthfully.
/// </summary>
public sealed class LobbyRepository : ILobbyRepository
{
    private readonly AppDbContext _db;

    public LobbyRepository(AppDbContext db) => _db = db;

    private static readonly MatchmakingTicketState[] NonTerminalTicketStates =
    {
        MatchmakingTicketState.Queued,
        MatchmakingTicketState.Claimed,
        MatchmakingTicketState.Requeued,
    };

    // ── Lobby reads ──────────────────────────────────────────────────────────

    public Task<Lobby?> GetForUpdateAsync(Guid lobbyId, CancellationToken ct = default) =>
        _db.Lobbies
            .Include(l => l.Members)
            .FirstOrDefaultAsync(l => l.Id == lobbyId, ct);

    public Task<Lobby?> GetByIdAsync(Guid lobbyId, CancellationToken ct = default) =>
        _db.Lobbies
            .AsNoTracking()
            .Include(l => l.Members)
            .FirstOrDefaultAsync(l => l.Id == lobbyId, ct);

    public async Task<IReadOnlyList<Lobby>> GetPublicPageAsync(
        int limit, DateTime? afterCreatedAt, Guid? afterId, CancellationToken ct = default)
    {
        // Open + Public only, matching ix_lobbies_public_discovery exactly. A private lobby is not filtered out of
        // a wider result set — it never enters the query, so it cannot influence page length or the cursor.
        var query = _db.Lobbies
            .AsNoTracking()
            .Include(l => l.Members)
            .Where(l => l.State == LobbyState.Open && l.Privacy == LobbyPrivacy.Public);

        if (afterCreatedAt is DateTime after && afterId is Guid afterGuid)
        {
            query = query.Where(l =>
                l.CreatedAt > after || (l.CreatedAt == after && l.Id.CompareTo(afterGuid) > 0));
        }

        return await query
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Lobby?> GetActiveLobbyForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var lobbyId = await _db.LobbyMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.State == LobbyMemberState.Joined)
            .Select(m => (Guid?)m.LobbyId)
            .FirstOrDefaultAsync(ct);

        if (lobbyId is null) return null;

        // A seat in a terminal lobby is not an active lobby. The member row survives a close/expiry (it is the
        // audit trail of who was there), so filtering on member state alone would report a finished lobby as live.
        return await _db.Lobbies
            .AsNoTracking()
            .Include(l => l.Members)
            .FirstOrDefaultAsync(
                l => l.Id == lobbyId
                     && (l.State == LobbyState.Open || l.State == LobbyState.Starting),
                ct);
    }

    public async Task<Guid?> GetActiveTicketIdForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.MatchmakingTickets
            .AsNoTracking()
            .Where(t => t.UserId == userId && NonTerminalTicketStates.Contains(t.State))
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

    // ── Credentials ──────────────────────────────────────────────────────────

    public Task<LobbyJoinCredential?> GetActiveCredentialAsync(Guid lobbyId, CancellationToken ct = default) =>
        _db.LobbyJoinCredentials
            .FirstOrDefaultAsync(
                c => c.LobbyId == lobbyId && c.State == LobbyCredentialState.Active, ct);

    // Both lookups match on the *digest*, never the plaintext, and return null for every failure mode alike. The
    // caller cannot distinguish "no such code" from "rotated" from "expired" — and neither can an attacker.
    public Task<LobbyJoinCredential?> FindActiveByCodeDigestAsync(
        string codeDigest, CancellationToken ct = default) =>
        _db.LobbyJoinCredentials
            .FirstOrDefaultAsync(
                c => c.CodeDigest == codeDigest && c.State == LobbyCredentialState.Active, ct);

    public Task<LobbyJoinCredential?> FindActiveByLinkTokenDigestAsync(
        string linkTokenDigest, CancellationToken ct = default) =>
        _db.LobbyJoinCredentials
            .FirstOrDefaultAsync(
                c => c.LinkTokenDigest == linkTokenDigest && c.State == LobbyCredentialState.Active, ct);

    // ── Invites ──────────────────────────────────────────────────────────────

    public Task<LobbyInvite?> GetInviteForUpdateAsync(Guid inviteId, CancellationToken ct = default) =>
        _db.LobbyInvites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);

    public Task<LobbyInvite?> GetPendingInviteAsync(
        Guid lobbyId, Guid inviteeUserId, CancellationToken ct = default) =>
        _db.LobbyInvites.FirstOrDefaultAsync(
            i => i.LobbyId == lobbyId
                 && i.InviteeUserId == inviteeUserId
                 && i.State == LobbyInviteState.Pending,
            ct);

    public async Task<IReadOnlyList<(LobbyInvite Invite, Lobby Lobby, User Inviter)>> GetPendingInvitesForUserAsync(
        Guid inviteeUserId, DateTime nowUtc, int limit, CancellationToken ct = default)
    {
        // Bounded and joined in one round trip. The dashboard's "N active" badge is exactly this query's count —
        // a count is never rendered without the authorized list it summarizes.
        var rows = await _db.LobbyInvites
            .AsNoTracking()
            .Where(i => i.InviteeUserId == inviteeUserId
                        && i.State == LobbyInviteState.Pending
                        && i.ExpiresAtUtc > nowUtc)
            .Join(_db.Lobbies.AsNoTracking(), i => i.LobbyId, l => l.Id, (i, l) => new { i, l })
            // An invite into a lobby that has since closed, started, or expired is dead — it must not appear as
            // actionable, even though its own row is still Pending until the expiry sweep reaches it.
            .Where(x => x.l.State == LobbyState.Open && x.l.ExpiresAtUtc > nowUtc)
            .Join(_db.Users.AsNoTracking(), x => x.i.InviterUserId, u => u.Id, (x, u) => new { x.i, x.l, u })
            .OrderByDescending(x => x.i.CreatedAt).ThenByDescending(x => x.i.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(x => (x.i, x.l, x.u)).ToList();
    }

    // ── Expiry sweep (slice 6C) ──────────────────────────────────────────────

    public async Task<IReadOnlyList<Lobby>> GetExpiredLobbiesAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default) =>
        await _db.Lobbies
            .Include(l => l.Members)
            .Where(l => (l.State == LobbyState.Open || l.State == LobbyState.Starting)
                        && l.ExpiresAtUtc <= nowUtc)
            .OrderBy(l => l.ExpiresAtUtc).ThenBy(l => l.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LobbyInvite>> GetExpiredInvitesAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default) =>
        await _db.LobbyInvites
            .Where(i => i.State == LobbyInviteState.Pending && i.ExpiresAtUtc <= nowUtc)
            .OrderBy(i => i.ExpiresAtUtc).ThenBy(i => i.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    // ── Start requests ───────────────────────────────────────────────────────

    public Task<LobbyStartRequest?> GetOpenStartRequestAsync(
        Guid lobbyId, int lobbyRevision, CancellationToken ct = default) =>
        _db.LobbyStartRequests.FirstOrDefaultAsync(
            r => r.LobbyId == lobbyId
                 && r.LobbyRevision == lobbyRevision
                 && r.State == LobbyStartRequestState.Open,
            ct);

    public Task<LobbyStartRequest?> GetStartRequestByIdempotencyKeyAsync(
        Guid lobbyId, string idempotencyKey, CancellationToken ct = default) =>
        _db.LobbyStartRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LobbyId == lobbyId && r.IdempotencyKey == idempotencyKey, ct);

    // ── Cross-module reads ───────────────────────────────────────────────────

    public Task<GameCapabilityProfile?> GetCapabilityProfileAsync(
        string gameSlug, int capabilityVersion, CancellationToken ct = default) =>
        _db.GameCapabilityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.GameSlug == gameSlug && p.CapabilityVersion == capabilityVersion, ct);

    public Task<GameCapabilityProfile?> GetActiveCapabilityProfileAsync(
        string gameSlug, CancellationToken ct = default) =>
        _db.GameCapabilityProfiles
            .AsNoTracking()
            .Where(p => p.GameSlug == gameSlug && p.IsActive)
            .OrderByDescending(p => p.CapabilityVersion)
            .FirstOrDefaultAsync(ct);

    public Task<Game?> GetGameAsync(string gameSlug, CancellationToken ct = default) =>
        _db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Slug == gameSlug, ct);

    public async Task<IReadOnlyList<string>> GetGameModesAsync(Guid gameId, CancellationToken ct = default) =>
        await _db.GameModeCapabilities
            .AsNoTracking()
            .Where(c => c.GameId == gameId)
            .Select(c => c.Mode)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetBlockedCounterpartsAsync(
        Guid userId, IReadOnlyList<Guid> candidateUserIds, CancellationToken ct = default)
    {
        if (candidateUserIds.Count == 0) return Array.Empty<Guid>();

        // Either direction. A block is symmetric in its effect on a lobby: it does not matter who blocked whom,
        // the two must not end up seated together.
        var blocked = await _db.Blocks
            .AsNoTracking()
            .Where(b =>
                (b.BlockerId == userId && candidateUserIds.Contains(b.BlockedId)) ||
                (b.BlockedId == userId && candidateUserIds.Contains(b.BlockerId)))
            .Select(b => b.BlockerId == userId ? b.BlockedId : b.BlockerId)
            .Distinct()
            .ToListAsync(ct);

        // The actor is trivially "not blocked with themselves"; a self-entry would make every roster look blocked.
        return blocked.Where(id => id != userId).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, User>();

        return await _db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
    }

    public Task<bool> AreFriendsAsync(Guid userA, Guid userB, CancellationToken ct = default) =>
        _db.Friendships.AsNoTracking().AnyAsync(
            f => f.Status == FriendshipStatus.Accepted
                 && ((f.RequesterId == userA && f.AddresseeId == userB)
                     || (f.RequesterId == userB && f.AddresseeId == userA)),
            ct);

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task AddLobbyAsync(
        Lobby lobby, LobbyJoinCredential credential, IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default)
    {
        await _db.Lobbies.AddAsync(lobby, ct);
        await _db.LobbyJoinCredentials.AddAsync(credential, ct);
        await StageAsync(events, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddInviteAsync(
        LobbyInvite invite, IReadOnlyList<OutboxMessage> events, CancellationToken ct = default)
    {
        await _db.LobbyInvites.AddAsync(invite, ct);
        await StageAsync(events, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddStartRequestAsync(
        LobbyStartRequest request, IReadOnlyList<OutboxMessage> events, CancellationToken ct = default)
    {
        await _db.LobbyStartRequests.AddAsync(request, ct);
        await StageAsync(events, ct);
        // The lobby's Open -> Starting transition is already tracked from the command's read, so this single
        // SaveChanges commits the state change and the MatchRequestedV1 row together or not at all.
        await _db.SaveChangesAsync(ct);
    }

    public async Task RotateCredentialAsync(
        LobbyJoinCredential outgoing, LobbyJoinCredential incoming, IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default)
    {
        // Both rows in one SaveChanges: there is no instant at which the old code is dead but the new one does not
        // exist, nor one at which both are redeemable.
        _db.LobbyJoinCredentials.Update(outgoing);
        await _db.LobbyJoinCredentials.AddAsync(incoming, ct);
        await StageAsync(events, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(IReadOnlyList<OutboxMessage> events, CancellationToken ct = default)
    {
        await StageAsync(events, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task StageAsync(IReadOnlyList<OutboxMessage> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await _db.OutboxMessages.AddRangeAsync(events, ct);
    }
}
