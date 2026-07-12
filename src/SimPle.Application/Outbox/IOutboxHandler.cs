using SimPle.Domain.Outbox;

namespace SimPle.Application.Outbox;

/// <summary>
/// One consumer of one or more integration-event types (<strong>D3</strong>).
///
/// <para>
/// Module 6 builds the codebase's first outbox <em>consumer</em> side. Module 3 has emitted <c>OutboxMessage</c>
/// rows since it shipped, and <see cref="OutboxDelivery"/> has always modelled per-<c>(eventId, handlerName)</c>
/// delivery state — but nothing has ever read them. M7, M8, and M11 inherit this machinery.
/// </para>
///
/// <para>
/// <strong>A handler must be idempotent.</strong> Delivery is at-least-once and always will be: the dispatcher
/// hands the event to the handler and then records the delivery, and no ordering of those two steps can be atomic
/// across a process crash. Recording first would lose events; recording after (as here) can repeat one. Repeating a
/// no-op is the only failure mode a correct handler can have, so that is the one this design chooses.
/// </para>
/// </summary>
public interface IOutboxHandler
{
    /// <summary>
    /// Stable identity, persisted in <see cref="OutboxDelivery.HandlerName"/>. <strong>Renaming it replays every
    /// event this handler has ever processed</strong>, because the delivery rows keyed to the old name no longer
    /// match — so it is a wire identifier, not a class name to be refactored freely.
    /// </summary>
    string HandlerName { get; }

    /// <summary>
    /// The <see cref="OutboxMessage.EventType"/> values this handler consumes.
    ///
    /// Declared as a list rather than a <c>Handles(string)</c> predicate for one concrete reason: the dispatcher has
    /// to turn it into a <c>WHERE event_type IN (…)</c>. A predicate cannot be translated to SQL, so the dispatcher
    /// would have to either keep a second, hand-maintained list of every event type in the system — which the next
    /// handler's author would forget to update, silently receiving nothing — or scan the entire outbox every cycle
    /// and filter in memory.
    /// </summary>
    IReadOnlyList<string> EventTypes { get; }

    /// <summary>
    /// Applies the event. Throwing means "retry me": the dispatcher records the attempt, releases the lease, and
    /// tries again on a later cycle until the retry budget is spent, after which the delivery is dead-lettered
    /// rather than retried forever.
    /// </summary>
    Task HandleAsync(OutboxMessage message, CancellationToken ct = default);
}
