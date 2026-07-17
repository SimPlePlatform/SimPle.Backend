using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Options;
using SimPle.Application.Expiry;
using SimPle.Infrastructure.Health;

namespace SimPle.Infrastructure.Matchmaking;

/// <summary>
/// Hosts the expiry sweep: tickets past 60 seconds, lobbies past 2 hours, invites past 30 minutes.
///
/// <para>
/// Unlike <see cref="MatchmakingWorker"/> this runs <strong>with or without Module 8</strong>, and that asymmetry is
/// the point. Matching without a match runtime would fabricate an opponent; expiring without one fabricates nothing.
/// It is what lets a Phase-1 player enqueue, watch their band widen, and receive an honest <c>TimedOut</c> — rather
/// than a ticket that sits <c>Queued</c> forever because the only thing that could ever have resolved it does not
/// exist yet.
/// </para>
/// </summary>
public sealed class LobbyExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExpiryOptions _options;
    private readonly ILogger<LobbyExpiryWorker> _logger;
    private readonly IWorkerReadinessRegistry _readiness;

    public LobbyExpiryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ExpiryOptions> options,
        ILogger<LobbyExpiryWorker> logger,
        IWorkerReadinessRegistry readiness)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _readiness = readiness;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _logger.LogInformation("Lobby expiry worker is disabled by configuration; not starting.");
            _readiness.MarkUnhealthy(RequiredWorkers.LobbyExpiry);
            return;
        }

        _readiness.MarkStarted(RequiredWorkers.LobbyExpiry);

        _logger.LogInformation(
            "Lobby expiry worker started. Interval={Interval} BatchSize={BatchSize}",
            _options.Interval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken);

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

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<IExpirySweeper>();

            var result = await sweeper.SweepAsync(ct);

            // The sweeper already logs the detail when it does work; a line per empty tick would drown it.
            if (result.Total > 0 && result.MaxTicketLag > TimeSpan.FromSeconds(5))
            {
                // The benchmark's budget. Crossing it does not break correctness — an expired ticket is already
                // unusable on read — but it means players are watching a dead ticket, so it is worth saying loudly.
                _logger.LogWarning(
                    "Expiry lag exceeded the 5s budget. MaxTicketLagMs={LagMs} Tickets={Tickets}",
                    (long)result.MaxTicketLag.TotalMilliseconds, result.TicketsExpired);
            }

            _readiness.MarkHealthy(RequiredWorkers.LobbyExpiry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readiness.MarkUnhealthy(RequiredWorkers.LobbyExpiry);
            // Idempotent by construction: every transition it drives is a TryExpire/TryTimeOut that returns false
            // rather than transitioning twice, so a failed sweep leaves nothing half-done and the next tick simply
            // sees the same overdue rows.
            _logger.LogError(ex, "Expiry sweep failed. Will retry in {Interval}.", _options.Interval);
        }
    }
}
