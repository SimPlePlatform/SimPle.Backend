namespace SimPle.Domain.Outbox;

/// <summary>
/// Generic per-handler activation watermark (docs/specs/module-07-realtime-presence-chat-spec.md, "Activation
/// watermark"). Built by Module 7 for <c>LobbyRealtimeHandler</c>; M8/M11 reuse it for their own consumers.
///
/// <para>
/// <c>OutboxRepository.BackfillMissingDeliveriesAsync</c> materializes a delivery row for <strong>every historical
/// message</strong> the moment a new handler registers. Without a watermark, a handler's first boot would replay
/// every historical event of its subscribed types as if it were live traffic. This row is created exactly once per
/// <see cref="HandlerName"/>: the first activation captures the outbox's own high-water mark
/// (<c>MAX(OccurredAtUtc)</c>, tie-broken by <see cref="WatermarkEventId"/>) at that moment, so pre-watermark
/// backfilled deliveries can be recognized and suppressed rather than acted on.
/// </para>
/// </summary>
public sealed class OutboxHandlerActivation
{
    /// <summary>Same identifier as <see cref="OutboxDelivery.HandlerName"/>. Renaming a handler orphans its
    /// activation row exactly the same way it orphans its delivery rows.</summary>
    public string HandlerName { get; private set; } = default!;

    public DateTime ActivatedAtUtc { get; private set; }
    public DateTime WatermarkOccurredAtUtc { get; private set; }

    /// <summary>Tie-break within the same <see cref="WatermarkOccurredAtUtc"/> instant. Null only when the outbox
    /// held zero matching events at activation time (nothing to tie-break against).</summary>
    public Guid? WatermarkEventId { get; private set; }

    private OutboxHandlerActivation() { }

    public static OutboxHandlerActivation Activate(
        string handlerName, DateTime activatedAtUtc, DateTime watermarkOccurredAtUtc, Guid? watermarkEventId) => new()
    {
        HandlerName = handlerName,
        ActivatedAtUtc = activatedAtUtc,
        WatermarkOccurredAtUtc = watermarkOccurredAtUtc,
        WatermarkEventId = watermarkEventId,
    };
}
