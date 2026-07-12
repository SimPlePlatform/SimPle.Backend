using Microsoft.EntityFrameworkCore;
using SimPle.Application.Common.Interfaces;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Repositories;

/// <summary>
/// The outbox's delivery side (<strong>D3</strong>). See <see cref="IOutboxRepository"/> for the contract and
/// <c>OutboxProcessor</c> for why leases are committed before any handler runs.
/// </summary>
public sealed class OutboxRepository : IOutboxRepository
{
    private readonly AppDbContext _db;

    public OutboxRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<(OutboxMessage Message, OutboxDelivery Delivery)>> LeaseAsync(
        string handlerName,
        IReadOnlyList<string> eventTypes,
        int batchSize,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        CancellationToken ct = default)
    {
        if (eventTypes.Count == 0) return Array.Empty<(OutboxMessage, OutboxDelivery)>();

        var types = eventTypes.ToArray();

        await BackfillMissingDeliveriesAsync(handlerName, types, batchSize, ct);

        var deliveryIds = await SelectLeasableDeliveryIdsAsync(handlerName, types, batchSize, nowUtc, ct);
        if (deliveryIds.Count == 0) return Array.Empty<(OutboxMessage, OutboxDelivery)>();

        var deliveries = await _db.OutboxDeliveries
            .Where(d => deliveryIds.Contains(d.Id))
            .ToListAsync(ct);

        var eventIds = deliveries.Select(d => d.EventId).ToList();
        var messages = await _db.OutboxMessages
            .AsNoTracking()
            .Where(m => eventIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var leased = new List<(OutboxMessage, OutboxDelivery)>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            if (!messages.TryGetValue(delivery.EventId, out var message)) continue;

            delivery.AcquireLease(leaseUntilUtc);
            leased.Add((message, delivery));
        }

        // The lease is written here and committed by the caller's transaction *before* any handler runs. If it were
        // written after, a crash mid-handler would leave the attempt uncounted and the row immediately retryable —
        // the retry budget would never advance and a poison event would be retried forever.
        await _db.SaveChangesAsync(ct);

        // Oldest event first, so a handler sees its events in roughly the order they happened.
        return leased
            .OrderBy(x => x.Item1.OccurredAtUtc).ThenBy(x => x.Item1.Id)
            .ToList();
    }

    /// <summary>
    /// Creates the delivery rows this handler is missing.
    ///
    /// <para>
    /// A message and its delivery rows are not written together, and cannot be: Module 3 has been emitting
    /// <c>UserBlockedV1</c> since long before any consumer existed, and a producer must not have to know who will
    /// eventually listen. So the dispatcher materializes its own rows on first sight — which also means a newly
    /// registered handler backfills the entire history of its event types, one bounded batch at a time.
    /// </para>
    ///
    /// <para>
    /// That backfill is safe precisely because handlers are idempotent and decide from <em>current</em> state:
    /// <c>LobbyBlockHandler</c> replaying a year-old block finds the two users share no lobby and does nothing. It
    /// is also why no activation watermark is needed — a second mechanism to get wrong, for no behavior gained.
    /// </para>
    ///
    /// <para>
    /// A concurrent dispatcher inserting the same row loses to the unique <c>(EventId, HandlerName)</c> index; that
    /// <c>23505</c> is contention, so the whole cycle is re-run and simply finds the row already there.
    /// </para>
    /// </summary>
    private async Task BackfillMissingDeliveriesAsync(
        string handlerName, string[] eventTypes, int batchSize, CancellationToken ct)
    {
        var missing = await _db.OutboxMessages
            .AsNoTracking()
            .Where(m => eventTypes.Contains(m.EventType))
            .Where(m => !_db.OutboxDeliveries.Any(d => d.EventId == m.Id && d.HandlerName == handlerName))
            .OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.Id)
            .Take(batchSize)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (missing.Count == 0) return;

        foreach (var eventId in missing)
            _db.OutboxDeliveries.Add(OutboxDelivery.Create(eventId, handlerName));

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The delivery rows this dispatcher may take: unprocessed, not dead-lettered, and not currently leased to a
    /// live dispatcher.
    ///
    /// <para>
    /// The lease is a <em>timestamp</em>, not a boolean, and that is what makes a crashed dispatcher recoverable: a
    /// lease that has lapsed is reclaimable by anyone, so work is never stranded by a process that died holding it.
    /// <c>FOR UPDATE … SKIP LOCKED</c> then ensures a second dispatcher steps over the rows this one is taking
    /// rather than blocking behind them.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SelectLeasableDeliveryIdsAsync(
        string handlerName, string[] eventTypes, int batchSize, DateTime nowUtc, CancellationToken ct)
    {
        // InMemory has neither raw SQL nor row locks. The dispatcher tests that use it assert bookkeeping —
        // backfill, attempt counting, dead-lettering, idempotent replay. The concurrent-lease behavior this SQL
        // exists for is asserted where it can actually be proven: against real PostgreSQL.
        if (!_db.Database.IsRelational())
        {
            return await _db.OutboxDeliveries
                .Where(d => d.HandlerName == handlerName
                            && !d.Processed
                            && !d.DeadLettered
                            && (d.Lease == null || d.Lease <= nowUtc))
                .Join(_db.OutboxMessages.Where(m => eventTypes.Contains(m.EventType)),
                    d => d.EventId, m => m.Id, (d, m) => new { d.Id, m.OccurredAtUtc })
                .OrderBy(x => x.OccurredAtUtc)
                .Take(batchSize)
                .Select(x => x.Id)
                .ToListAsync(ct);
        }

        // FOR UPDATE OF d — lock the delivery rows only. The joined message rows are immutable and read-only here;
        // locking them too would serialize unrelated handlers against each other for no reason.
        return await _db.Database
            .SqlQuery<Guid>($"""
                SELECT d."Id"
                FROM outbox_deliveries d
                JOIN outbox_messages m ON m."Id" = d."EventId"
                WHERE d."HandlerName" = {handlerName}
                  AND d."Processed" = false
                  AND d."DeadLettered" = false
                  AND (d."Lease" IS NULL OR d."Lease" <= {nowUtc})
                  AND m."EventType" = ANY({eventTypes})
                ORDER BY m."OccurredAtUtc", m."Id"
                LIMIT {batchSize}
                FOR UPDATE OF d SKIP LOCKED
                """)
            .ToListAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    public async Task<TimeSpan?> GetOldestPendingAgeAsync(
        string handlerName, IReadOnlyList<string> eventTypes, DateTime nowUtc, CancellationToken ct = default)
    {
        if (eventTypes.Count == 0) return null;

        var types = eventTypes.ToArray();

        // Counts a message with no delivery row yet as pending — otherwise a dispatcher that had stopped before it
        // could even create its rows would report a lag of zero, which is the exact failure the signal exists to
        // catch.
        var oldest = await _db.OutboxMessages
            .AsNoTracking()
            .Where(m => types.Contains(m.EventType))
            .Where(m => !_db.OutboxDeliveries.Any(d =>
                d.EventId == m.Id && d.HandlerName == handlerName && (d.Processed || d.DeadLettered)))
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => (DateTime?)m.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        return oldest is null ? null : nowUtc - oldest.Value;
    }
}
