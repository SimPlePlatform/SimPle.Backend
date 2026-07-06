namespace SimPle.Domain.Outbox;

/// <summary>
/// Per-(EventId, HandlerName) delivery state for the transactional outbox. There is deliberately NO global
/// "processed" marker on <see cref="OutboxMessage"/>: each registered handler tracks its own lease, attempt
/// count, processed/dead-letter status and last safe error, so one slow or failing consumer can never starve
/// another. A message is retained until every required handler acknowledges (or its replay window expires).
/// M3 emits messages; delivery rows are created and advanced by the dispatcher for registered handlers.
/// In M3 no consumers are active yet (M7/M10/M11 deferred), so this table is created but stays empty.
/// </summary>
public class OutboxDelivery
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid EventId { get; private set; }
    public string HandlerName { get; private set; } = default!;

    /// <summary>Lease expiry while a dispatcher is attempting delivery; null when free.</summary>
    public DateTime? Lease { get; private set; }
    public int AttemptCount { get; private set; }
    public bool Processed { get; private set; }
    public bool DeadLettered { get; private set; }

    /// <summary>Safe error text only — never PII, credentials, or event body.</summary>
    public string? LastError { get; private set; }

    private OutboxDelivery() { }

    public static OutboxDelivery Create(Guid eventId, string handlerName) => new()
    {
        Id = Guid.NewGuid(),
        EventId = eventId,
        HandlerName = handlerName,
        AttemptCount = 0,
        Processed = false,
        DeadLettered = false,
    };

    public void AcquireLease(DateTime leaseUntil)
    {
        Lease = leaseUntil;
        AttemptCount += 1;
    }

    public void MarkProcessed()
    {
        Processed = true;
        Lease = null;
        LastError = null;
    }

    public void MarkFailed(string safeError, bool deadLetter)
    {
        Lease = null;
        LastError = safeError;
        DeadLettered = deadLetter;
    }
}
