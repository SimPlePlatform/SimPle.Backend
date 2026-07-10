using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Friends;
using SimPle.Domain.Friends;
using SimPle.Domain.Outbox;
using SimPle.Domain.Profiles;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Repositories;

public sealed class FriendRepository : IFriendRepository
{
    private readonly AppDbContext _db;

    public FriendRepository(AppDbContext db) => _db = db;

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    // ── Reads: single edges / counts ────────────────────────────────────────────

    public Task<Friendship?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Friendships.FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<Friendship?> GetEdgeAsync(Guid userA, Guid userB, CancellationToken ct = default) =>
        _db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterId == userA && f.AddresseeId == userB) ||
            (f.RequesterId == userB && f.AddresseeId == userA), ct);

    public Task<bool> AreFriendsAsync(Guid userA, Guid userB, CancellationToken ct = default) =>
        _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == userA && f.AddresseeId == userB) ||
             (f.RequesterId == userB && f.AddresseeId == userA)), ct);

    public Task<int> GetFriendCountAsync(Guid userId, CancellationToken ct = default) =>
        _db.Friendships.CountAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            (f.RequesterId == userId || f.AddresseeId == userId), ct);

    public Task<int> GetIncomingRequestCountAsync(Guid userId, CancellationToken ct = default) =>
        _db.Friendships.CountAsync(f =>
            f.Status == FriendshipStatus.Pending && f.AddresseeId == userId, ct);

    public Task<int> GetOutgoingRequestCountAsync(Guid userId, CancellationToken ct = default) =>
        _db.Friendships.CountAsync(f =>
            f.Status == FriendshipStatus.Pending && f.RequesterId == userId, ct);

    public async Task<int> GetMutualFriendCountAsync(Guid userA, Guid userB, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var friendsOfA = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userA || f.AddresseeId == userA))
            .Select(f => f.RequesterId == userA ? f.AddresseeId : f.RequesterId);

        var friendsOfB = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userB || f.AddresseeId == userB))
            .Select(f => f.RequesterId == userB ? f.AddresseeId : f.RequesterId);

        var mutualIds = friendsOfA.Intersect(friendsOfB);

        // M03-008 fix: this is a symmetric helper called from both viewer-orderings (and from contexts with
        // no single fixed viewer), so a mutual candidate is excluded if EITHER party has a block with them —
        // matches GetVisibleMutualFriendsPageAsync's privacy/suspension filter, conservatively applied to
        // both sides rather than assuming which of userA/userB is "the viewer".
        return await _db.Users
            .Where(u => mutualIds.Contains(u.Id) &&
                        u.Visibility != ProfileVisibility.Private &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        !_db.Blocks.Any(b =>
                            (b.BlockerId == userA && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == userA) ||
                            (b.BlockerId == userB && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == userB)))
            .CountAsync(ct);
    }

    public async Task<int> GetVisibleFriendCountAsync(Guid targetUserId, Guid viewerId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var candidateIds = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == targetUserId || f.AddresseeId == targetUserId))
            .Select(f => f.RequesterId == targetUserId ? f.AddresseeId : f.RequesterId);

        // M03-008 fix: mirrors GetVisibleFriendsPageAsync's filter set exactly (visibility, suspension,
        // blocks) so the count can never exceed what the paged list actually discloses.
        return await _db.Users
            .Where(u => candidateIds.Contains(u.Id) &&
                        u.Visibility != ProfileVisibility.Private &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        !_db.Blocks.Any(b =>
                            (b.BlockerId == viewerId && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == viewerId)))
            .CountAsync(ct);
    }

    // ── Reads: keyset cursor pages ──────────────────────────────────────────────

    public async Task<IReadOnlyList<(Friendship f, User other)>> GetFriendsPageAsync(
        Guid userId, string? normalizedQuery, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default)
    {
        var asRequester = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && f.RequesterId == userId)
            .Join(_db.Users, f => f.AddresseeId, u => u.Id, (f, u) => new { f, u });

        var asAddressee = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && f.AddresseeId == userId)
            .Join(_db.Users, f => f.RequesterId, u => u.Id, (f, u) => new { f, u });

        var combined = asRequester.Union(asAddressee);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            combined = combined.Where(x =>
                x.u.NormalizedUsername.Contains(normalizedQuery) ||
                x.u.DisplayName.ToUpper().Contains(normalizedQuery));
        }

        // Keyset on (UPPER(displayName), userId) ascending.
        if (afterDisplayName is not null && afterId is Guid ai)
        {
            combined = combined.Where(x =>
                x.u.DisplayName.ToUpper().CompareTo(afterDisplayName) > 0 ||
                (x.u.DisplayName.ToUpper() == afterDisplayName && x.u.Id.CompareTo(ai) > 0));
        }

        var rows = await combined
            .OrderBy(x => x.u.DisplayName.ToUpper())
            .ThenBy(x => x.u.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(x => (x.f, x.u)).ToList();
    }

    public async Task<IReadOnlyList<(Friendship f, User requester, User addressee, int mutualCount)>> GetRequestsPageAsync(
        Guid userId, string direction, int limit,
        DateTime? afterSentAt, Guid? afterId, CancellationToken ct = default)
    {
        var actorFriendIds = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId);

        var baseQuery = direction == "outgoing"
            ? _db.Friendships.Where(f => f.Status == FriendshipStatus.Pending && f.RequesterId == userId)
            : _db.Friendships.Where(f => f.Status == FriendshipStatus.Pending && f.AddresseeId == userId);

        // Keyset on (SentAt DESC, Id DESC).
        if (afterSentAt is DateTime asa && afterId is Guid ai)
        {
            baseQuery = baseQuery.Where(f =>
                f.SentAt < asa || (f.SentAt == asa && f.Id.CompareTo(ai) < 0));
        }

        var rows = await baseQuery
            .OrderByDescending(f => f.SentAt).ThenByDescending(f => f.Id)
            .Take(limit)
            .Join(_db.Users, f => f.RequesterId, u => u.Id, (f, req) => new { f, req })
            .Join(_db.Users, x => x.f.AddresseeId, u => u.Id, (x, addr) => new { x.f, x.req, addr })
            .Select(x => new
            {
                x.f,
                x.req,
                x.addr,
                MutualCount = _db.Friendships.Count(mf =>
                    mf.Status == FriendshipStatus.Accepted &&
                    (mf.RequesterId == x.req.Id || mf.AddresseeId == x.req.Id) &&
                    actorFriendIds.Contains(
                        mf.RequesterId == x.req.Id ? mf.AddresseeId : mf.RequesterId))
            })
            .OrderByDescending(x => x.f.SentAt).ThenByDescending(x => x.f.Id)
            .ToListAsync(ct);

        return rows.Select(x => (x.f, x.req, x.addr, x.MutualCount)).ToList();
    }

    public async Task<IReadOnlyList<(Block b, User blocked)>> GetBlocksPageAsync(
        Guid blockerId, int limit, DateTime? afterCreatedAt, Guid? afterId, CancellationToken ct = default)
    {
        var query = _db.Blocks.Where(b => b.BlockerId == blockerId);

        if (afterCreatedAt is DateTime aca && afterId is Guid ai)
        {
            query = query.Where(b =>
                b.CreatedAt < aca || (b.CreatedAt == aca && b.Id.CompareTo(ai) < 0));
        }

        var rows = await query
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
            .Take(limit)
            .Join(_db.Users, b => b.BlockedId, u => u.Id, (b, u) => new { b, u })
            .OrderByDescending(x => x.b.CreatedAt).ThenByDescending(x => x.b.Id)
            .ToListAsync(ct);

        return rows.Select(x => (x.b, x.u)).ToList();
    }

    public async Task<IReadOnlyList<User>> GetVisibleFriendsPageAsync(
        Guid targetUserId, Guid viewerId, string? normalizedQuery, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var asRequester = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && f.RequesterId == targetUserId)
            .Select(f => f.AddresseeId);
        var asAddressee = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && f.AddresseeId == targetUserId)
            .Select(f => f.RequesterId);
        var candidateIds = asRequester.Union(asAddressee);

        var query = _db.Users
            .Where(u => candidateIds.Contains(u.Id) &&
                        u.Visibility != ProfileVisibility.Private &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        !_db.Blocks.Any(b =>
                            (b.BlockerId == viewerId && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == viewerId)));

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            query = query.Where(u =>
                u.NormalizedUsername.Contains(normalizedQuery) ||
                u.DisplayName.ToUpper().Contains(normalizedQuery));
        }

        if (afterDisplayName is not null && afterId is Guid ai)
        {
            query = query.Where(u =>
                u.DisplayName.ToUpper().CompareTo(afterDisplayName) > 0 ||
                (u.DisplayName.ToUpper() == afterDisplayName && u.Id.CompareTo(ai) > 0));
        }

        return await query
            .OrderBy(u => u.DisplayName.ToUpper())
            .ThenBy(u => u.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> GetVisibleMutualFriendsPageAsync(
        Guid viewerId, Guid targetUserId, int limit,
        string? afterDisplayName, Guid? afterId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var friendsOfViewer = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == viewerId || f.AddresseeId == viewerId))
            .Select(f => f.RequesterId == viewerId ? f.AddresseeId : f.RequesterId);

        var friendsOfTarget = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == targetUserId || f.AddresseeId == targetUserId))
            .Select(f => f.RequesterId == targetUserId ? f.AddresseeId : f.RequesterId);

        var mutualIds = friendsOfViewer.Intersect(friendsOfTarget);

        var query = _db.Users
            .Where(u => mutualIds.Contains(u.Id) &&
                        u.Visibility != ProfileVisibility.Private &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        !_db.Blocks.Any(b =>
                            (b.BlockerId == viewerId && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == viewerId)));

        if (afterDisplayName is not null && afterId is Guid ai)
        {
            query = query.Where(u =>
                u.DisplayName.ToUpper().CompareTo(afterDisplayName) > 0 ||
                (u.DisplayName.ToUpper() == afterDisplayName && u.Id.CompareTo(ai) > 0));
        }

        return await query
            .OrderBy(u => u.DisplayName.ToUpper())
            .ThenBy(u => u.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(User user, int bucket, int mutualCount, string relationshipState)>> SearchPeopleAsync(
        Guid viewerId, string normalizedQuery, int limit,
        int? afterBucket, string? afterSortKey, Guid? afterId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var actorFriendIds = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == viewerId || f.AddresseeId == viewerId))
            .Select(f => f.RequesterId == viewerId ? f.AddresseeId : f.RequesterId);

        var candidates = _db.Users
            .Where(u => u.Id != viewerId &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        (u.NormalizedUsername == normalizedQuery ||
                         u.NormalizedUsername.StartsWith(normalizedQuery) ||
                         u.DisplayName.ToUpper().StartsWith(normalizedQuery)) &&
                        !_db.Blocks.Any(b =>
                            (b.BlockerId == viewerId && b.BlockedId == u.Id) ||
                            (b.BlockerId == u.Id && b.BlockedId == viewerId)))
            .GroupJoin(_db.UserFriendSettings, u => u.Id, s => s.UserId,
                (u, settings) => new { User = u, Search = settings.Select(s => s.SearchVisibility).FirstOrDefault() })
            .Select(x => new
            {
                x.User,
                x.Search,
                Bucket = x.User.NormalizedUsername == normalizedQuery ? 0
                        : x.User.NormalizedUsername.StartsWith(normalizedQuery) ? 1
                        : 2,
                // M03-008 fix: a mutual candidate must itself be visible to the viewer (not private,
                // not suspended, not blocked either way) or it must not count toward the disclosed total.
                MutualCount = _db.Friendships.Count(mf =>
                    mf.Status == FriendshipStatus.Accepted &&
                    (mf.RequesterId == x.User.Id || mf.AddresseeId == x.User.Id) &&
                    actorFriendIds.Contains(mf.RequesterId == x.User.Id ? mf.AddresseeId : mf.RequesterId) &&
                    _db.Users.Any(mu =>
                        mu.Id == (mf.RequesterId == x.User.Id ? mf.AddresseeId : mf.RequesterId) &&
                        mu.Visibility != ProfileVisibility.Private &&
                        !(mu.IsSuspended && (mu.SuspendedUntil == null || mu.SuspendedUntil > now))) &&
                    !_db.Blocks.Any(b =>
                        (b.BlockerId == viewerId && b.BlockedId == (mf.RequesterId == x.User.Id ? mf.AddresseeId : mf.RequesterId)) ||
                        (b.BlockerId == (mf.RequesterId == x.User.Id ? mf.AddresseeId : mf.RequesterId) && b.BlockedId == viewerId))),
            })
            .Where(x => x.Search != SearchVisibility.Nobody &&
                        (x.Search != SearchVisibility.FriendsOfFriends || x.MutualCount > 0));

        if (afterBucket is int ab && afterSortKey is not null && afterId is Guid aid)
        {
            candidates = candidates.Where(x =>
                x.Bucket > ab ||
                (x.Bucket == ab && x.User.NormalizedUsername.CompareTo(afterSortKey) > 0) ||
                (x.Bucket == ab && x.User.NormalizedUsername == afterSortKey && x.User.Id.CompareTo(aid) > 0));
        }

        var rows = await candidates
            .OrderBy(x => x.Bucket).ThenBy(x => x.User.NormalizedUsername).ThenBy(x => x.User.Id)
            .Take(limit)
            .Select(x => new
            {
                x.User,
                x.Bucket,
                x.MutualCount,
                RelationshipState = _db.Friendships
                    .Where(f => (f.RequesterId == viewerId && f.AddresseeId == x.User.Id) ||
                                (f.RequesterId == x.User.Id && f.AddresseeId == viewerId))
                    .Select(f => f.Status == FriendshipStatus.Accepted ? "Friends"
                                : f.Status == FriendshipStatus.Pending && f.RequesterId == viewerId ? "OutgoingPending"
                                : f.Status == FriendshipStatus.Pending ? "IncomingPending"
                                : "None")
                    .FirstOrDefault() ?? "None"
            })
            .ToListAsync(ct);

        return rows.Select(x => (x.User, x.Bucket, x.MutualCount, x.RelationshipState)).ToList();
    }

    public async Task<IReadOnlyList<(User user, int mutualCount)>> GetSuggestionsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var excludedByFriendship = _db.Friendships
            .Where(f => f.Status != FriendshipStatus.Declined &&
                        f.Status != FriendshipStatus.Cancelled &&
                        (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId);

        var excludedByBlock = _db.Blocks
            .Where(b => b.BlockerId == userId || b.BlockedId == userId)
            .Select(b => b.BlockerId == userId ? b.BlockedId : b.BlockerId);

        var excludedByDismissal = _db.DismissedFriendSuggestions
            .Where(d => d.UserId == userId && d.ExpiresAt > now)
            .Select(d => d.SuggestedUserId);

        var actorFriendIds = _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId);

        var candidates = await _db.Users
            .Where(u => u.Id != userId &&
                        u.Visibility != ProfileVisibility.Private &&
                        !(u.IsSuspended && (u.SuspendedUntil == null || u.SuspendedUntil > now)) &&
                        !excludedByFriendship.Contains(u.Id) &&
                        !excludedByBlock.Contains(u.Id) &&
                        !excludedByDismissal.Contains(u.Id))
            .GroupJoin(_db.UserFriendSettings, u => u.Id, s => s.UserId,
                (u, settings) => new
                {
                    User = u,
                    Privacy = settings.Select(s => s.FriendRequestPrivacy).FirstOrDefault()
                })
            .Select(x => new
            {
                x.User,
                x.Privacy,
                MutualCount = _db.Friendships.Count(mf =>
                    mf.Status == FriendshipStatus.Accepted &&
                    (mf.RequesterId == x.User.Id || mf.AddresseeId == x.User.Id) &&
                    actorFriendIds.Contains(
                        mf.RequesterId == x.User.Id ? mf.AddresseeId : mf.RequesterId))
            })
            .Where(x => x.Privacy != FriendRequestPrivacy.Off &&
                        (x.Privacy != FriendRequestPrivacy.FriendsOfFriends || x.MutualCount > 0))
            .OrderByDescending(x => x.MutualCount)
            .ThenBy(x => x.User.DisplayName.ToUpper())
            .ThenBy(x => x.User.Id)
            .Take(limit)
            .ToListAsync(ct);

        return candidates.Select(x => (x.User, x.MutualCount)).ToList();
    }

    // ── Friendship writes (stage outbox atomically) ─────────────────────────────

    public async Task<AddFriendshipOutcome> TryAddFriendshipAsync(
        Friendship f, OutboxMessage created, CancellationToken ct = default)
    {
        try
        {
            await _db.Friendships.AddAsync(f, ct);
            await _db.OutboxMessages.AddAsync(created, ct);
            await PostgresRetry.SaveChangesAsync(_db, ct);
            return AddFriendshipOutcome.Added;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            Detach(f, created);
            return AddFriendshipOutcome.Conflict;
        }
    }

    public async Task<UpdateFriendshipOutcome> TryUpdateFriendshipAsync(
        Friendship f, OutboxMessage evt, CancellationToken ct = default)
    {
        try
        {
            _db.Friendships.Update(f);
            await _db.OutboxMessages.AddAsync(evt, ct);
            await PostgresRetry.SaveChangesAsync(_db, ct);
            return UpdateFriendshipOutcome.Updated;
        }
        catch (DbUpdateConcurrencyException)
        {
            Detach(f, evt);
            return UpdateFriendshipOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException)
        {
            // Outbox uniqueness rejected a retried transition, or a racing writer won: re-read and converge.
            Detach(f, evt);
            return UpdateFriendshipOutcome.ConcurrencyConflict;
        }
    }

    // ── Block writes ────────────────────────────────────────────────────────────

    public async Task<AddBlockOutcome> BlockAndCancelFriendshipAsync(
        Block block, Friendship? endedEdge, OutboxMessage blockedEvent, OutboxMessage? removedEvent,
        CancellationToken ct = default)
    {
        try
        {
            await _db.Blocks.AddAsync(block, ct);
            if (endedEdge is not null) _db.Friendships.Update(endedEdge);
            await _db.OutboxMessages.AddAsync(blockedEvent, ct);
            if (removedEvent is not null) await _db.OutboxMessages.AddAsync(removedEvent, ct);
            await PostgresRetry.SaveChangesAsync(_db, ct);   // single unit of work
            return AddBlockOutcome.Added;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.Entry(block).State = EntityState.Detached;
            _db.Entry(blockedEvent).State = EntityState.Detached;
            if (removedEvent is not null) _db.Entry(removedEvent).State = EntityState.Detached;
            return AddBlockOutcome.Conflict;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AddBlockOutcome.ConcurrencyConflict;
        }
    }

    public Task<Block?> GetBlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default) =>
        _db.Blocks.FirstOrDefaultAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId, ct);

    public Task<bool> IsBlockedInEitherDirectionAsync(Guid userA, Guid userB, CancellationToken ct = default) =>
        _db.Blocks.AnyAsync(b =>
            (b.BlockerId == userA && b.BlockedId == userB) ||
            (b.BlockerId == userB && b.BlockedId == userA), ct);

    public async Task RemoveBlockAsync(Block b, OutboxMessage unblockedEvent, CancellationToken ct = default)
    {
        _db.Blocks.Remove(b);
        await _db.OutboxMessages.AddAsync(unblockedEvent, ct);
        await PostgresRetry.SaveChangesAsync(_db, ct);
    }

    // ── Suggestion dismissals ───────────────────────────────────────────────────

    public Task<DismissedFriendSuggestion?> GetDismissalAsync(
        Guid userId, Guid suggestedUserId, CancellationToken ct = default) =>
        _db.DismissedFriendSuggestions.FirstOrDefaultAsync(
            d => d.UserId == userId && d.SuggestedUserId == suggestedUserId, ct);

    public async Task UpsertDismissalAsync(
        Guid userId, Guid suggestedUserId, DateTime expiresAt, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await _db.DismissedFriendSuggestions.FirstOrDefaultAsync(
                d => d.UserId == userId && d.SuggestedUserId == suggestedUserId, ct);
            if (existing is not null)
            {
                existing.Renew(expiresAt);
                await _db.SaveChangesAsync(ct);
                return;
            }

            var row = DismissedFriendSuggestion.Create(userId, suggestedUserId, expiresAt);
            try
            {
                await _db.DismissedFriendSuggestions.AddAsync(row, ct);
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(row).State = EntityState.Detached;   // concurrent first insert — renew on retry
            }
        }

        throw new InvalidOperationException(
            $"Failed to upsert dismissal ({userId}->{suggestedUserId}) after retries.");
    }

    public async Task<int> DeleteExpiredDismissalsAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default)
    {
        var batch = await _db.DismissedFriendSuggestions
            .Where(d => d.ExpiresAt <= nowUtc)
            .OrderBy(d => d.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        _db.DismissedFriendSuggestions.RemoveRange(batch);
        await _db.SaveChangesAsync(ct);
        return batch.Count;
    }

    // ── Settings ───────────────────────────────────────────────────────────────

    public Task<UserFriendSettings?> GetSettingsAsync(Guid userId, CancellationToken ct = default) =>
        _db.UserFriendSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);

    public async Task<UserFriendSettings> UpsertSettingsAsync(
        Guid userId, FriendRequestPrivacy privacy, SearchVisibility? searchVisibility,
        FriendsListVisibility? friendsListVisibility, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await _db.UserFriendSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
            if (existing is not null)
            {
                existing.UpdateSettings(privacy, searchVisibility, friendsListVisibility);
                await _db.SaveChangesAsync(ct);
                return existing;
            }

            var newSettings = UserFriendSettings.CreateDefault(userId);
            newSettings.UpdateSettings(privacy, searchVisibility, friendsListVisibility);
            try
            {
                await _db.UserFriendSettings.AddAsync(newSettings, ct);
                await _db.SaveChangesAsync(ct);
                return newSettings;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(newSettings).State = EntityState.Detached;   // concurrent insert — retry once
            }
        }

        throw new InvalidOperationException(
            $"Failed to upsert UserFriendSettings for user {userId} after retries.");
    }

    private void Detach(Friendship f, OutboxMessage evt)
    {
        _db.Entry(f).State = EntityState.Detached;
        _db.Entry(evt).State = EntityState.Detached;
    }
}
