using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SimPle.Infrastructure.Persistence;

/// <summary>
/// Bounded whole-operation retry for the transient PostgreSQL contention codes that the engine cannot retry
/// safely on its own: <c>40001</c> (serialization_failure) and <c>40P01</c> (deadlock_detected). The
/// unordered-pair <c>23505</c> (unique_violation) is deliberately NOT retried here — it is a meaningful
/// outcome (cross-send convergence / already-blocked) that the caller maps to a domain result, so it must
/// bubble to the caller's own <c>catch</c>. After the budget is exhausted the original exception propagates
/// and the service surfaces <c>Friends.ConcurrencyConflict</c>.
/// </summary>
public static class PostgresRetry
{
    private const int MaxAttempts = 3;

    public static async Task SaveChangesAsync(DbContext db, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && IsTransientContention(ex))
            {
                // The failed statement's transaction is already rolled back; the tracked changes are intact,
                // so a bounded re-issue of SaveChanges is safe. Tiny linear backoff to let the winner commit.
                await Task.Delay(15 * attempt, ct);
            }
        }
    }

    private static bool IsTransientContention(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg &&
        pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
}
