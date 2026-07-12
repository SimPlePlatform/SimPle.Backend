using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Lobbies.Services;
using SimPle.Shared.Common;

namespace SimPle.Infrastructure.Persistence;

/// <summary>
/// Reconciliation <strong>R3</strong>: reruns a whole lobby command — read, decide, <em>and</em> write — on
/// contention, then surfaces a typed conflict rather than a 500.
///
/// <para>
/// <see cref="PostgresRetry"/> is deliberately left untouched and is <em>not</em> reused here. It re-issues only
/// the <c>SaveChanges</c> call, which is right for its existing callers (they catch <c>23505</c> themselves as a
/// meaningful domain outcome) and wrong for M6: a last-seat join that lost the race must <em>re-read</em> to
/// discover the lobby is now full. Replaying only the save would re-commit a decision made against state that no
/// longer holds.
/// </para>
///
/// <para>
/// Between attempts the change tracker is cleared. Without that, the rerun would re-decide against the losing
/// attempt's tracked entities — it would look like a re-read and behave like a replay, which is the subtlest way
/// to get this wrong.
/// </para>
/// </summary>
public sealed class LobbyCommandRunner : ILobbyCommandRunner
{
    private readonly AppDbContext _db;
    private readonly ILogger<LobbyCommandRunner> _logger;

    /// <summary>
    /// Three attempts. Contention here is between a handful of humans clicking "join" on the same last seat, not
    /// a high-throughput write path — if three serialized attempts all lose, the honest answer is a typed conflict,
    /// not a longer queue.
    /// </summary>
    private const int MaxAttempts = 3;

    public LobbyCommandRunner(AppDbContext db, ILogger<LobbyCommandRunner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<T>> RunAsync<T>(
        Guid actorUserId,
        Func<CancellationToken, Task<Result<T>>> command,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(actorUserId, command, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsContention(ex))
            {
                // The failed transaction is already rolled back. Discard every entity the losing attempt tracked
                // so the rerun genuinely re-reads current state instead of re-deciding against stale ones.
                _db.ChangeTracker.Clear();

                _logger.LogInformation(
                    "Lobby command contention; rerunning whole command. ActorId={ActorId} Attempt={Attempt} Reason={Reason}",
                    actorUserId, attempt, ex.GetType().Name);

                // Tiny linear backoff so the winner commits before the loser re-reads; otherwise the rerun could
                // read the same pre-commit snapshot and lose again for the same reason.
                await Task.Delay(15 * attempt, ct);
            }
            catch (Exception ex) when (IsContention(ex))
            {
                // Budget spent. A typed 409 — never a 500 (brief Risk #5).
                _db.ChangeTracker.Clear();

                _logger.LogWarning(
                    ex,
                    "Lobby command exhausted its retry budget. ActorId={ActorId} Attempts={Attempts}",
                    actorUserId, MaxAttempts);

                return Result<T>.Fail(
                    LobbyErrors.ConcurrencyConflict,
                    "That lobby is being changed by someone else right now. Please try again.");
            }
        }
    }

    private async Task<Result<T>> RunOnceAsync<T>(
        Guid actorUserId,
        Func<CancellationToken, Task<Result<T>>> command,
        CancellationToken ct)
    {
        // The EF InMemory provider (used by the HTTP-contract integration tests) has no transactions and no raw
        // SQL. Those tests assert routing, auth, validation, and error mapping — none of which the transaction
        // affects. The race behavior this class exists for is asserted where it can actually be proven: against
        // real PostgreSQL.
        if (!_db.Database.IsRelational())
            return await command(ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Serialize this actor's seat-acquiring commands against each other for the life of the transaction.
        //
        // This is what makes the cross-table "one active lobby OR one active ticket" check real. The two filtered
        // unique indexes live on different tables and cannot see each other (brief Risk #2), so under READ
        // COMMITTED a concurrent join and enqueue would each read "nothing active" — neither seeing the other's
        // uncommitted row — and both would commit. The lock closes that window without paying SERIALIZABLE's
        // retry cost on every unrelated lobby write.
        //
        // It is transaction-scoped: PostgreSQL releases it at commit or rollback, so a crashed command cannot
        // leak it.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLockKey(actorUserId)})", ct);

        var result = await command(ct);

        // A Result.Fail is a *decision* ("you are already in a lobby"), not a fault. Nothing was written, so the
        // commit is a no-op — but it must still happen, to release the advisory lock promptly rather than holding
        // it until disposal.
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// A stable 64-bit lock key from the actor's id. Distinct users colliding on the same key is harmless — the
    /// only consequence is that two unrelated actors briefly serialize — so the first 8 bytes are enough and no
    /// cryptographic hash is warranted.
    /// </summary>
    private static long AdvisoryLockKey(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes[..8]);
    }

    /// <summary>
    /// Shared with <see cref="WorkerTransaction"/> — see <see cref="PostgresContention"/> for why the predicate
    /// lives in one place. The <c>xmin</c> row-version case is the one that catches concurrent last-seat joins;
    /// omitting it would leave the exact race this module is built around surfacing as a 500.
    /// </summary>
    private static bool IsContention(Exception ex) => PostgresContention.IsContention(ex);
}
