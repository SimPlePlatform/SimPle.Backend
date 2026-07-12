using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;

namespace SimPle.Application.Outbox;

/// <summary>What one dispatcher pass did, per handler. Counts and durations only — never an event body.</summary>
public sealed record OutboxDispatchResult(
    int Leased,
    int Processed,
    int Failed,
    int DeadLettered,
    TimeSpan? OldestPendingAge)
{
    public static readonly OutboxDispatchResult Idle = new(0, 0, 0, 0, null);
}

/// <summary>
/// The transactional outbox's dispatcher (<strong>D3</strong>) — the codebase's first.
///
/// <para>
/// Leased, bounded, and idempotent, in that order of importance:
/// </para>
/// <list type="bullet">
///   <item><strong>Leased</strong> — a delivery row is claimed with <c>FOR UPDATE SKIP LOCKED</c> and stamped with
///     an expiry, so two dispatcher instances never work the same event, and a dispatcher that dies mid-handler
///     releases its work by letting the lease lapse rather than by holding a lock forever.</item>
///   <item><strong>Bounded</strong> — a fixed batch per pass, and a retry budget per delivery. A handler that fails
///     permanently is dead-lettered instead of being retried until the end of time, which is what keeps one broken
///     consumer from consuming the whole dispatcher.</item>
///   <item><strong>Idempotent</strong> — delivery is at-least-once. The handler runs, and only then is the delivery
///     recorded; a crash in between replays the event. Every handler is written to make that a no-op, because no
///     ordering of "act" and "record" is atomic across a process boundary and pretending otherwise is how outboxes
///     lose events.</item>
/// </list>
///
/// <para>
/// Each handler is dispatched in <em>its own</em> transaction. Batching them would mean one handler's failure rolls
/// back another's recorded success and replays it — the precise starvation the per-<c>(eventId, handlerName)</c>
/// delivery row exists to prevent.
/// </para>
/// </summary>
public interface IOutboxProcessor
{
    Task<OutboxDispatchResult> DispatchAsync(IOutboxHandler handler, CancellationToken ct = default);
}

/// <inheritdoc cref="IOutboxProcessor"/>
public sealed class OutboxProcessor : IOutboxProcessor
{
    private readonly IOutboxRepository _outbox;
    private readonly IWorkerTransaction _transaction;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IOutboxRepository outbox,
        IWorkerTransaction transaction,
        IOptions<OutboxOptions> options,
        TimeProvider clock,
        ILogger<OutboxProcessor> logger)
    {
        _outbox = outbox;
        _transaction = transaction;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<OutboxDispatchResult> DispatchAsync(IOutboxHandler handler, CancellationToken ct = default)
    {
        var eventTypes = handler.EventTypes;
        if (eventTypes.Count == 0) return OutboxDispatchResult.Idle;

        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        // The lease is taken in its own transaction and committed before any handler runs. If it were held open
        // across the handler call, a slow handler would keep the delivery rows locked, and the second dispatcher's
        // SKIP LOCKED would skip them — which is correct — but a crash would then roll the *lease* back too, and
        // the row would be retried immediately with its attempt count reset. Committing the lease first is what
        // makes the retry budget real.
        var leased = await _transaction.RunAsync(
            token => _outbox.LeaseAsync(
                handler.HandlerName, eventTypes, _options.BatchSize,
                nowUtc, nowUtc + _options.LeaseDuration, token),
            ct);

        if (leased.Count == 0)
        {
            var idleAge = await _outbox.GetOldestPendingAgeAsync(handler.HandlerName, eventTypes, nowUtc, ct);
            return OutboxDispatchResult.Idle with { OldestPendingAge = idleAge };
        }

        var processed = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var (message, delivery) in leased)
        {
            try
            {
                await handler.HandleAsync(message, ct);
                delivery.MarkProcessed();
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The budget is spent once this attempt is the last one. AttemptCount was already incremented by
                // AcquireLease, so it counts attempts *made*, not attempts remaining.
                var isFinalAttempt = delivery.AttemptCount >= _options.MaxAttempts;

                // Never the exception message: an event body or a user id could reach it, and a dead-letter row is
                // long-lived, widely read, and exactly the kind of place PII quietly accumulates.
                delivery.MarkFailed($"{ex.GetType().Name} after {delivery.AttemptCount} attempt(s).", isFinalAttempt);

                failed++;
                if (isFinalAttempt) deadLettered++;

                _logger.Log(
                    isFinalAttempt ? LogLevel.Error : LogLevel.Warning,
                    ex,
                    "Outbox delivery failed. Handler={Handler} EventType={EventType} Attempt={Attempt} DeadLettered={DeadLettered}",
                    handler.HandlerName, message.EventType, delivery.AttemptCount, isFinalAttempt);
            }
        }

        // Saved directly, NOT through IWorkerTransaction. Its retry clears the change tracker between attempts, which
        // is right for a delegate that re-reads — and catastrophic here: the delivery rows this pass mutated live
        // only in the tracker, so a retry would detach them and then dutifully save nothing, silently losing every
        // MarkProcessed and MarkFailed in the batch. If this save fails, the leases simply lapse and the batch is
        // retried on a later cycle, which is what leases are for.
        await _outbox.SaveAsync(ct);

        var oldestPending = await _outbox.GetOldestPendingAgeAsync(
            handler.HandlerName, eventTypes, _clock.GetUtcNow().UtcDateTime, ct);

        return new OutboxDispatchResult(leased.Count, processed, failed, deadLettered, oldestPending);
    }
}
