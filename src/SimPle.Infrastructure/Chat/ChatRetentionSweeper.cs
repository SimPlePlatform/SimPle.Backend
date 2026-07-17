using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Chat;
using SimPle.Infrastructure.Health;

namespace SimPle.Infrastructure.Chat;

/// <summary>
/// The retention sweep (docs/specs/module-07-realtime-presence-chat-spec.md, "Ownership, retention, deletion" and
/// Risk #5): periodically deletes chat messages whose <c>RetainUntilUtc</c> (30 days) has passed and which carry
/// no active <see cref="SimPle.Domain.Chat.ChatMessageHold"/>, via <see cref="IChatRepository.DeleteExpiredAsync"/>.
/// Modeled directly on <c>SimPle.Infrastructure.Auth.TokenCleanupService</c> — same shape, same readiness
/// contract, same stagger-then-loop structure.
/// </summary>
public sealed class ChatRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatRetentionSweeper> _logger;
    private readonly ChatRetentionOptions _options;
    private readonly IWorkerReadinessRegistry _readiness;
    private readonly TimeProvider _clock;

    public ChatRetentionSweeper(
        IServiceScopeFactory scopeFactory,
        ILogger<ChatRetentionSweeper> logger,
        IOptions<ChatRetentionOptions> options,
        IWorkerReadinessRegistry readiness,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _readiness = readiness;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _readiness.MarkStarted(RequiredWorkers.ChatRetention);

        // Stagger the first run so it doesn't run immediately on startup, matching TokenCleanupService.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSweepAsync(stoppingToken);
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatRepository>();

            var deleted = await chat.DeleteExpiredAsync(nowUtc, _options.BatchSize, ct);
            if (deleted > 0)
                _logger.LogInformation(
                    "Chat retention sweep: deleted {Count} expired message(s) (cutoff: {Cutoff:u}).",
                    deleted, nowUtc);

            _readiness.MarkHealthy(RequiredWorkers.ChatRetention);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readiness.MarkUnhealthy(RequiredWorkers.ChatRetention);
            _logger.LogError(ex, "Chat retention sweep failed. Will retry in {Interval}.", _options.Interval);
        }
    }
}
