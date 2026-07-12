using Microsoft.EntityFrameworkCore;
using SimPle.Application.Common.Interfaces;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Repositories;

/// <summary>
/// Matchmaking data access.
///
/// Like <see cref="LobbyRepository"/>, nothing here catches a unique-violation or row-version exception —
/// contention propagates to <see cref="WorkerTransaction"/> / <see cref="LobbyCommandRunner"/>, which re-run the
/// whole unit of work so it can re-read and answer truthfully rather than deciding against stale state (R3).
/// </summary>
public sealed class MatchmakingRepository : IMatchmakingRepository
{
    private readonly AppDbContext _db;

    public MatchmakingRepository(AppDbContext db) => _db = db;

    private static readonly MatchmakingTicketState[] NonTerminalStates =
    {
        MatchmakingTicketState.Queued,
        MatchmakingTicketState.Claimed,
        MatchmakingTicketState.Requeued,
    };

    // ── Ticket reads ─────────────────────────────────────────────────────────

    public Task<MatchmakingTicket?> GetTicketAsync(Guid ticketId, CancellationToken ct = default) =>
        _db.MatchmakingTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);

    public Task<MatchmakingTicket?> GetTicketForUpdateAsync(Guid ticketId, CancellationToken ct = default) =>
        _db.MatchmakingTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);

    public Task<MatchmakingTicket?> GetActiveTicketForUserAsync(Guid userId, CancellationToken ct = default) =>
        _db.MatchmakingTickets.FirstOrDefaultAsync(
            t => t.UserId == userId && NonTerminalStates.Contains(t.State), ct);

    public Task<MatchmakingAssignment?> GetActiveAssignmentAsync(Guid ticketId, CancellationToken ct = default) =>
        _db.MatchmakingAssignments.AsNoTracking().FirstOrDefaultAsync(
            a => a.TicketId == ticketId && a.State == MatchmakingAssignmentState.Active, ct);

    // ── Worker claim ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MatchmakingTicket>> ClaimQueuedTicketsAsync(
        int batchSize, DateTime nowUtc, CancellationToken ct = default)
    {
        // EF cannot express FOR UPDATE SKIP LOCKED, so the *selection* is raw SQL and the load is not: we take the
        // row locks on exactly the ids we want, then materialize those ids through the normal tracked query so the
        // entities behave like any other aggregate the command layer mutates.
        //
        // On InMemory (the HTTP-contract tests) there is no row locking to take. Those tests assert routing, auth,
        // and error mapping; the claim races this method exists for are asserted where they can actually be
        // proven — against real PostgreSQL.
        if (!_db.Database.IsRelational())
        {
            return await _db.MatchmakingTickets
                .Where(t => t.State == MatchmakingTicketState.Queued && t.DeadlineAtUtc > nowUtc)
                .OrderBy(t => t.EnqueuedAtUtc).ThenBy(t => t.Id)
                .Take(batchSize)
                .ToListAsync(ct);
        }

        // Oldest first: this is what anchors proposals on the longest-waiting ticket, which is half the
        // anti-starvation guarantee (the widening bands are the other half).
        //
        // SKIP LOCKED means a second worker steps over rows this one holds instead of blocking behind them — that
        // is a throughput property, NOT exclusivity (Risk #1). The partial unique index on active assignment is
        // what makes double assignment impossible.
        //
        // "State" is stored as text, not an int: MatchmakingConfiguration declares HasConversion<string>(), and the
        // partial indexes' own filters are written against the string ('Queued'). Comparing it to an enum ordinal
        // here would be a type error at best and, if it had coerced, would have silently matched nothing — a worker
        // that claims no tickets and reports a healthy empty cycle.
        var queued = nameof(MatchmakingTicketState.Queued);

        var ids = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT "Id"
                FROM matchmaking_tickets
                WHERE "State" = {queued}
                  AND "DeadlineAtUtc" > {nowUtc}
                ORDER BY "EnqueuedAtUtc", "Id"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (ids.Count == 0) return Array.Empty<MatchmakingTicket>();

        var tickets = await _db.MatchmakingTickets
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(ct);

        // Preserve the locked order — the caller anchors on the first one.
        return tickets
            .OrderBy(t => t.EnqueuedAtUtc).ThenBy(t => t.Id)
            .ToList();
    }

    // ── Expiry sweep ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MatchmakingTicket>> GetExpiredTicketsAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default) =>
        await _db.MatchmakingTickets
            .Where(t => NonTerminalStates.Contains(t.State) && t.DeadlineAtUtc <= nowUtc)
            .OrderBy(t => t.DeadlineAtUtc).ThenBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(ct);

    // ── Observability ────────────────────────────────────────────────────────

    public async Task<TimeSpan?> GetOldestQueuedAgeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var oldest = await _db.MatchmakingTickets
            .AsNoTracking()
            .Where(t => t.State == MatchmakingTicketState.Queued)
            .OrderBy(t => t.EnqueuedAtUtc)
            .Select(t => (DateTime?)t.EnqueuedAtUtc)
            .FirstOrDefaultAsync(ct);

        return oldest is null ? null : nowUtc - oldest.Value;
    }

    // ── Cross-module reads ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<(Guid A, Guid B)>> GetBlockedPairsAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count < 2) return Array.Empty<(Guid, Guid)>();

        // Both endpoints must be in the batch: a block against somebody who is not in this queue cycle cannot
        // affect any proposal it could form, and loading it would be work with no consequence.
        var rows = await _db.Blocks
            .AsNoTracking()
            .Where(b => userIds.Contains(b.BlockerId) && userIds.Contains(b.BlockedId))
            .Select(b => new { b.BlockerId, b.BlockedId })
            .ToListAsync(ct);

        return rows.Select(r => (r.BlockerId, r.BlockedId)).ToList();
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task AddTicketAsync(MatchmakingTicket ticket, CancellationToken ct = default)
    {
        await _db.MatchmakingTickets.AddAsync(ticket, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddAssignmentsAsync(
        IReadOnlyList<MatchmakingAssignment> assignments,
        IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default)
    {
        await _db.MatchmakingAssignments.AddRangeAsync(assignments, ct);
        await _db.OutboxMessages.AddRangeAsync(events, ct);

        // The tickets' Claimed -> Matched transitions are already tracked from the claim, so this single
        // SaveChanges commits the state changes, the assignments, and the MatchRequestedV1 rows together or not at
        // all. There is no instant at which a ticket is Matched with no assignment, or an assignment exists with no
        // event for M8 to consume.
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(IReadOnlyList<OutboxMessage> events, CancellationToken ct = default)
    {
        if (events.Count > 0) await _db.OutboxMessages.AddRangeAsync(events, ct);
        await _db.SaveChangesAsync(ct);
    }
}
