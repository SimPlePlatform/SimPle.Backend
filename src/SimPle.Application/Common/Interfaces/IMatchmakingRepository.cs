using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Data access for matchmaking tickets and assignments.
///
/// Like <see cref="ILobbyRepository"/>, nothing here catches a unique-violation or row-version exception:
/// contention propagates to the bounded whole-command retry, which re-reads and answers truthfully instead of
/// deciding against stale state (R3).
/// </summary>
public interface IMatchmakingRepository
{
    // ── Ticket reads ─────────────────────────────────────────────────────────

    /// <summary>Untracked. For rendering a status response.</summary>
    Task<MatchmakingTicket?> GetTicketAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Tracked. The read half of a read-decide-write command (cancel).</summary>
    Task<MatchmakingTicket?> GetTicketForUpdateAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// The user's single nonterminal ticket, tracked, or null.
    ///
    /// This is the queue's half of the cross-table "one active lobby <strong>or</strong> one active ticket"
    /// invariant. It is only sound inside the command runner's transaction-scoped advisory lock on the actor —
    /// the ticket index and the lobby-member index live on different tables and cannot see each other (Risk #2).
    /// </summary>
    Task<MatchmakingTicket?> GetActiveTicketForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The active assignment for a ticket, or null. Used to project a matched ticket's handoff.</summary>
    Task<MatchmakingAssignment?> GetActiveAssignmentAsync(Guid ticketId, CancellationToken ct = default);

    // ── Worker claim ─────────────────────────────────────────────────────────

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> queued, not-yet-expired tickets for this worker cycle, using
    /// <c>SELECT … FOR UPDATE SKIP LOCKED</c> so two workers never block on — or double-claim — the same row.
    /// Oldest first, which is what anchors proposals on the longest-waiting ticket (anti-starvation).
    ///
    /// <para>
    /// <strong>The row lock is not exclusivity</strong> (Risk #1). It stops two workers <em>contending</em> on one
    /// row; a requeued ticket or a serialization retry can still attempt a second assignment. The partial unique
    /// index <c>UNIQUE (TicketId) WHERE State = 'Active'</c> is the correctness boundary that makes double
    /// assignment impossible, and the two-worker test asserts against exactly that.
    /// </para>
    ///
    /// <para>
    /// Must be called inside a transaction: the lock is transaction-scoped, so a worker crash rolls the claim back
    /// and returns the ticket to <c>Queued</c> with no compensating action and no lease to expire.
    /// </para>
    ///
    /// Returns <em>tracked</em> entities — the caller mutates and saves them in the same unit of work.
    /// </summary>
    Task<IReadOnlyList<MatchmakingTicket>> ClaimQueuedTicketsAsync(
        int batchSize, DateTime nowUtc, CancellationToken ct = default);

    // ── Expiry sweep ─────────────────────────────────────────────────────────

    /// <summary>Nonterminal tickets past their deadline, tracked, bounded. Oldest first.</summary>
    Task<IReadOnlyList<MatchmakingTicket>> GetExpiredTicketsAsync(
        DateTime nowUtc, int batchSize, CancellationToken ct = default);

    // ── Observability ────────────────────────────────────────────────────────

    /// <summary>
    /// The age of the oldest queued ticket, or null when the queue is empty. Backs the
    /// <c>matchmaking-queue-age</c> signal — the one number that says whether the queue is healthy without
    /// exposing who is in it.
    /// </summary>
    Task<TimeSpan?> GetOldestQueuedAgeAsync(DateTime nowUtc, CancellationToken ct = default);

    // ── Cross-module reads ───────────────────────────────────────────────────

    /// <summary>
    /// Every M3 block among <paramref name="userIds"/>, in one round trip.
    ///
    /// Deliberately not <see cref="ILobbyRepository.GetBlockedCounterpartsAsync"/> called once per ticket owner:
    /// that is the right shape for a lobby roster (one actor against a handful of members) and the wrong one for a
    /// worker batch, where it would issue one query per ticket every cycle — a self-inflicted load problem that
    /// grows exactly when the queue is busiest.
    /// </summary>
    Task<IReadOnlyList<(Guid A, Guid B)>> GetBlockedPairsAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    // ── Writes ───────────────────────────────────────────────────────────────

    Task AddTicketAsync(MatchmakingTicket ticket, CancellationToken ct = default);

    /// <summary>
    /// Commits one proposal: every member ticket's state change, one <see cref="MatchmakingAssignment"/> per ticket
    /// sharing a group id, and the single <c>MatchRequestedV1</c> — all in one <c>SaveChanges</c>, so an assignment
    /// can never exist without its event, nor an event without its assignment.
    /// </summary>
    Task AddAssignmentsAsync(
        IReadOnlyList<MatchmakingAssignment> assignments,
        IReadOnlyList<OutboxMessage> events,
        CancellationToken ct = default);

    /// <summary>Persists whatever the caller mutated on already-tracked entities, plus any events.</summary>
    Task SaveAsync(IReadOnlyList<OutboxMessage> events, CancellationToken ct = default);
}
