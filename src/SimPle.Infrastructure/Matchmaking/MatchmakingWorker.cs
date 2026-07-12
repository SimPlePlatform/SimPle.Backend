using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Options;
using SimPle.Application.Matchmaking.Services;

namespace SimPle.Infrastructure.Matchmaking;

/// <summary>
/// Hosts the matching loop. Deliberately thin: every decision lives in <see cref="IMatchmakingCoordinator"/>, and
/// this class only decides <em>when</em> to ask for one.
///
/// <para>
/// That split is what makes the module's central guarantee provable. "Two competing workers never double-assign a
/// ticket" is asserted by running two coordinators concurrently against real PostgreSQL — which is possible only
/// because a cycle is a callable method, not a timer tick buried in a hosted service.
/// </para>
///
/// <para>
/// The worker <em>identity</em> is per-process and stable for the process's life. It is recorded on every ticket it
/// claims and survives onto terminal rows, which is what lets a worker that consistently loses its handoffs be
/// identified from the data alone (the <c>matchmaking-worker-failure</c> signal) instead of by correlating logs.
/// </para>
/// </summary>
public sealed class MatchmakingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MatchmakingOptions _options;
    private readonly ILogger<MatchmakingWorker> _logger;
    private readonly string _workerId;

    public MatchmakingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MatchmakingOptions> options,
        ILogger<MatchmakingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;

        // Machine name plus a random suffix: two instances on one host must not share an id, or their claims become
        // indistinguishable in exactly the situation the id exists to disambiguate. Budgeted to fit
        // matchmaking_tickets."ClaimedByWorker" (varchar(64)) exactly: 31 + 1 + 32.
        var host = Environment.MachineName;
        if (host.Length > 31) host = host[..31];
        _workerId = $"{host}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            // The rollback plan calls for disabling the workers while preserving every lobby and ticket record.
            // Not hosting the loop is how that is done; nothing else changes.
            _logger.LogInformation("Matchmaking worker is disabled by configuration; not starting.");
            return;
        }

        _logger.LogInformation(
            "Matchmaking worker started. WorkerId={WorkerId} Interval={Interval} BatchSize={BatchSize}",
            _workerId, _options.Interval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);

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

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<IMatchmakingCoordinator>();

            var result = await coordinator.RunCycleAsync(_workerId, ct);

            // Only log a cycle that did something. Before Module 8 the coordinator returns Disabled on every tick,
            // and a line for each would bury every other signal in the log within minutes.
            if (result.TicketsMatched > 0)
            {
                _logger.LogInformation(
                    "Matchmaking cycle. Worker={WorkerId} Claimed={Claimed} Proposals={Proposals} Matched={Matched} OldestQueuedMs={OldestMs}",
                    _workerId, result.TicketsClaimed, result.ProposalsFormed, result.TicketsMatched,
                    (long?)result.OldestQueuedAge?.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed cycle is survivable by construction: the transaction rolled back, which released the
            // FOR UPDATE SKIP LOCKED row locks, which returned every claimed ticket to Queued. There is nothing to
            // compensate and nothing to clean up — the next cycle simply sees them again.
            _logger.LogError(
                ex, "Matchmaking cycle failed; claimed tickets were rolled back to Queued. Worker={WorkerId}", _workerId);
        }
    }
}
