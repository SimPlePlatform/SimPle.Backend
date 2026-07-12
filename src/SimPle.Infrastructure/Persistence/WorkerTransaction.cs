using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimPle.Application.Common.Interfaces;

namespace SimPle.Infrastructure.Persistence;

/// <summary>
/// <see cref="IWorkerTransaction"/> over <see cref="AppDbContext"/>.
///
/// Mirrors <see cref="LobbyCommandRunner"/>'s retry semantics and shares its contention predicate
/// (<see cref="PostgresContention"/>), but takes no advisory lock — see <see cref="IWorkerTransaction"/> for why.
/// </summary>
public sealed class WorkerTransaction : IWorkerTransaction
{
    private readonly AppDbContext _db;
    private readonly ILogger<WorkerTransaction> _logger;

    private const int MaxAttempts = 3;

    public WorkerTransaction(AppDbContext db, ILogger<WorkerTransaction> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(work, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && PostgresContention.IsContention(ex))
            {
                // The failed transaction is already rolled back — including any FOR UPDATE SKIP LOCKED row locks it
                // held, which returns the claimed tickets to Queued with no compensating write and no lease to
                // expire. Discard the losing attempt's tracked entities so the rerun genuinely re-reads.
                _db.ChangeTracker.Clear();

                _logger.LogInformation(
                    "Worker transaction contention; rerunning cycle. Attempt={Attempt} Reason={Reason}",
                    attempt, ex.GetType().Name);

                await Task.Delay(15 * attempt, ct);
            }
        }
    }

    private async Task<T> RunOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        // The EF InMemory provider has no transactions and no raw SQL. Worker tests that use it assert cycle
        // bookkeeping only; every claim/assignment race this class exists for is asserted where it can actually be
        // proven — against real PostgreSQL.
        if (!_db.Database.IsRelational())
            return await work(ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var result = await work(ct);

        await transaction.CommitAsync(ct);
        return result;
    }
}
