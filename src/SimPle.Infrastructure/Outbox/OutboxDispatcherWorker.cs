using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Options;
using SimPle.Application.Outbox;
using SimPle.Infrastructure.Health;

namespace SimPle.Infrastructure.Outbox;

/// <summary>
/// Hosts the transactional-outbox dispatcher (<strong>D3</strong>) — the codebase's first.
///
/// <para>
/// Module 3 has emitted outbox rows since it shipped and <see cref="SimPle.Domain.Outbox.OutboxDelivery"/> has always
/// modelled per-<c>(eventId, handlerName)</c> delivery state, but nothing has ever read them. This loop is what turns
/// that table from a design into a delivery guarantee, and M7/M8/M11 inherit it by registering an
/// <see cref="IOutboxHandler"/> — they do not each build their own consumer.
/// </para>
///
/// <para>
/// Each registered handler is dispatched independently. One handler failing, dead-lettering, or being slow must not
/// hold up another: that is exactly the starvation the per-handler delivery row exists to prevent, and batching them
/// into a shared pass would quietly reintroduce it.
/// </para>
/// </summary>
public sealed class OutboxDispatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly IWorkerReadinessRegistry _readiness;

    public OutboxDispatcherWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        TimeProvider clock,
        ILogger<OutboxDispatcherWorker> logger,
        IWorkerReadinessRegistry readiness)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _readiness = readiness;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _logger.LogInformation("Outbox dispatcher is disabled by configuration; not starting.");
            _readiness.MarkUnhealthy(RequiredWorkers.OutboxDispatcher);
            return;
        }

        // Every handler's activation watermark (docs/specs/module-07-realtime-presence-chat-spec.md, "Activation
        // watermark") must be captured here, before the loop below ever leases a message — not lazily, inside
        // HandleAsync, on whichever pass first happens to find something leasable. GetOrActivateAsync's watermark
        // is MAX(OccurredAtUtc, Id) over the outbox *at the moment its query runs*; if that query is deferred until
        // a live event has already landed, the query sees its own event as the table's current maximum and
        // classifies it as pre-existing history, silently dropping it rather than fanning it out. Activating here,
        // before this process can have leased anything, keeps the watermark anchored to "before this process
        // existed" rather than to an arbitrary later instant that a fast-moving live event can race into.
        await ActivateAllHandlersAsync(stoppingToken);

        _readiness.MarkStarted(RequiredWorkers.OutboxDispatcher);

        _logger.LogInformation(
            "Outbox dispatcher started. Interval={Interval} BatchSize={BatchSize} MaxAttempts={MaxAttempts}",
            _options.Interval, _options.BatchSize, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchAllAsync(stoppingToken);

            try
            {
                await Task.Delay(_options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ActivateAllHandlersAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var activation = scope.ServiceProvider.GetRequiredService<IOutboxActivationStore>();
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>();
            var nowUtc = _clock.GetUtcNow().UtcDateTime;

            foreach (var handler in handlers)
            {
                if (ct.IsCancellationRequested) break;
                await activation.GetOrActivateAsync(handler.HandlerName, handler.EventTypes, nowUtc, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: an un-activated handler simply activates lazily on its first HandleAsync call instead,
            // which is the pre-existing (racy) behavior this method exists to avoid — not a new failure mode.
            _logger.LogError(ex, "Eager outbox handler activation failed; handlers will activate lazily instead.");
        }
    }

    private async Task DispatchAllAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToList();
            var allHandlersSucceeded = true;

            foreach (var handler in handlers)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // A fresh scope per handler: the processor and the handler share a scoped AppDbContext, and one
                    // handler's failed save must not leave a dirty change tracker for the next one to trip over.
                    await using var handlerScope = _scopeFactory.CreateAsyncScope();
                    var processor = handlerScope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                    var scopedHandler = handlerScope.ServiceProvider
                        .GetServices<IOutboxHandler>()
                        .First(h => h.HandlerName == handler.HandlerName);

                    var result = await processor.DispatchAsync(scopedHandler, ct);

                    if (result.Processed > 0 || result.Failed > 0)
                    {
                        _logger.LogInformation(
                            "Outbox pass. Handler={Handler} Leased={Leased} Processed={Processed} Failed={Failed} DeadLettered={DeadLettered} OldestPendingMs={OldestMs}",
                            handler.HandlerName, result.Leased, result.Processed, result.Failed, result.DeadLettered,
                            (long?)result.OldestPendingAge?.TotalMilliseconds);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    allHandlersSucceeded = false;
                    // Nothing is lost: any delivery this pass leased keeps its lease only until it lapses, after which
                    // another pass reclaims it. That is precisely why the lease is a timestamp and not a boolean.
                    _logger.LogError(
                        ex, "Outbox dispatch pass failed. Handler={Handler}. Leases will lapse and be retried.",
                        handler.HandlerName);
                }
            }

            if (allHandlersSucceeded)
                _readiness.MarkHealthy(RequiredWorkers.OutboxDispatcher);
            else
                _readiness.MarkUnhealthy(RequiredWorkers.OutboxDispatcher);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readiness.MarkUnhealthy(RequiredWorkers.OutboxDispatcher);
            _logger.LogError(ex, "Outbox dispatcher health check failed. Will retry in {Interval}.", _options.Interval);
        }
    }
}
