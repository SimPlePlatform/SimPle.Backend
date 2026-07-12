using SimPle.Domain.Outbox;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Data access for the transactional outbox's <em>delivery</em> side (<strong>D3</strong>). The producing side —
/// staging <see cref="OutboxMessage"/> rows inside the transaction that caused them — belongs to each module's own
/// repository and is not here.
/// </summary>
public interface IOutboxRepository
{
    /// <summary>
    /// Leases up to <paramref name="batchSize"/> undelivered messages for one handler, oldest first.
    ///
    /// <para>
    /// "Undelivered" means: the message's type is one this handler consumes, and it has no
    /// <see cref="OutboxDelivery"/> row for this handler that is <c>Processed</c>, <c>DeadLettered</c>, or currently
    /// leased to a live dispatcher. There is deliberately no global "processed" flag on the message — each handler
    /// tracks its own progress, so one slow or failing consumer can never starve another, and a message is retained
    /// until every registered handler has acknowledged it.
    /// </para>
    ///
    /// <para>
    /// The lease is what makes two dispatcher instances safe. It is taken with <c>FOR UPDATE SKIP LOCKED</c> over
    /// the delivery rows in the same transaction that writes the lease expiry, so a second dispatcher skips rows the
    /// first is holding rather than blocking on them — and an expired lease (a crashed dispatcher) is reclaimable,
    /// which is why it is a timestamp and not a boolean.
    /// </para>
    ///
    /// <para>
    /// Returns the messages together with their (created-or-reclaimed) delivery rows, tracked. The caller marks each
    /// one processed or failed and saves.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<(OutboxMessage Message, OutboxDelivery Delivery)>> LeaseAsync(
        string handlerName,
        IReadOnlyList<string> eventTypes,
        int batchSize,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        CancellationToken ct = default);

    /// <summary>Persists the delivery-row state changes the dispatcher made.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// How far behind the oldest unprocessed delivery is, or null when there is none. Backs the <c>outbox lag</c>
    /// signal: a dispatcher that has quietly stopped looks exactly like an idle one until this number grows.
    /// </summary>
    Task<TimeSpan?> GetOldestPendingAgeAsync(
        string handlerName, IReadOnlyList<string> eventTypes, DateTime nowUtc, CancellationToken ct = default);
}
