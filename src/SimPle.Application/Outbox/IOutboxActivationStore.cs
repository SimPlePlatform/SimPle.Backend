using SimPle.Domain.Outbox;

namespace SimPle.Application.Outbox;

/// <summary>Data access for the per-handler activation watermark (docs/specs/module-07-realtime-presence-chat-
/// spec.md, "Activation watermark"). See <see cref="OutboxHandlerActivation"/> for why this exists.</summary>
public interface IOutboxActivationStore
{
    /// <summary>
    /// Returns this handler's activation row, creating it on first call. The watermark captured on that first
    /// call is <c>MAX(OccurredAtUtc, Id)</c> over <paramref name="eventTypes"/> in the outbox at that moment —
    /// everything at or before it is pre-existing history the handler must never replay as live traffic.
    ///
    /// <para>
    /// Computed only on the creating call, inside the same idempotent-insert attempt — never on a call that finds
    /// the row already present, so the watermark is always the activation moment, never a later re-check.
    /// Concurrent first-activation callers race on the unique <c>HandlerName</c> primary key: the loser's insert
    /// catches <c>23505</c> and simply re-reads the winner's row rather than computing its own.
    /// </para>
    /// </summary>
    Task<OutboxHandlerActivation> GetOrActivateAsync(
        string handlerName,
        IReadOnlyList<string> eventTypes,
        DateTime nowUtc,
        CancellationToken ct = default);
}
