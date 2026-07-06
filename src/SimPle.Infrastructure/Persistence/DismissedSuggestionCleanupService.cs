using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;

namespace SimPle.Infrastructure.Persistence;

// Background service that periodically deletes expired friend-suggestion dismissals.
// A dismissal suppresses a suggestion for 30 days; once expired, the row is dead weight and the
// candidate becomes suggestible again. Runs daily by default, deleting in bounded batches so a large
// backlog never issues one unbounded DELETE.
public sealed class DismissedSuggestionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DismissedSuggestionCleanupService> _logger;
    private readonly DismissedSuggestionCleanupOptions _options;

    public DismissedSuggestionCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<DismissedSuggestionCleanupService> logger,
        IOptions<DismissedSuggestionCleanupOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first run so it doesn't run immediately on startup.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCleanupAsync(stoppingToken);
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFriendRepository>();

            var batchSize = _options.BatchSize > 0 ? _options.BatchSize : 500;
            var now = DateTime.UtcNow;
            var total = 0;

            // Loop until a short page signals the backlog is drained (or cancellation).
            while (!ct.IsCancellationRequested)
            {
                var deleted = await repo.DeleteExpiredDismissalsAsync(now, batchSize, ct);
                total += deleted;
                if (deleted < batchSize) break;
            }

            if (total > 0)
                _logger.LogInformation(
                    "Dismissed-suggestion cleanup: deleted {Count} expired rows (cutoff: {Cutoff:u})",
                    total, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dismissed-suggestion cleanup failed. Will retry in {Interval}.", _options.Interval);
        }
    }
}
