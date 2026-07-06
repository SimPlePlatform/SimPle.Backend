namespace SimPle.Domain.Outbox;

/// <summary>
/// Immutable transactional-outbox row. Staged in the same PostgreSQL transaction as the aggregate mutation
/// that produced it, so the state change and the event are committed or rolled back atomically. The unique
/// (AggregateId, EventType, AggregateDomainVersion) index makes a retried transition idempotent — it cannot
/// stage the same logical event twice. Payloads carry minimum ids only: no profile snapshot, no block reason.
/// M3 owns this storage and the writer; consumers (M7/M10/M11) own their own delivery/inbox effects via
/// <see cref="OutboxDelivery"/> and declare their own activation watermark.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string AggregateType { get; private set; } = default!;
    public Guid AggregateId { get; private set; }
    public string EventType { get; private set; } = default!;
    public int EventVersion { get; private set; }
    public long AggregateDomainVersion { get; private set; }
    public int RequestCycleId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Serialized event body (stored as PostgreSQL jsonb). Minimum ids only.</summary>
    public string Payload { get; private set; } = default!;

    private OutboxMessage() { }

    public static OutboxMessage Create(
        string aggregateType,
        Guid aggregateId,
        string eventType,
        int eventVersion,
        long aggregateDomainVersion,
        int requestCycleId,
        string payload) => new()
    {
        Id = Guid.NewGuid(),
        AggregateType = aggregateType,
        AggregateId = aggregateId,
        EventType = eventType,
        EventVersion = eventVersion,
        AggregateDomainVersion = aggregateDomainVersion,
        RequestCycleId = requestCycleId,
        OccurredAtUtc = DateTime.UtcNow,
        Payload = payload,
    };
}
