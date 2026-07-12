using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SimPle.Infrastructure.Persistence;

/// <summary>
/// The three ways PostgreSQL says "someone else got there first". All of them mean the same thing to a caller:
/// your read is stale, look again.
///
/// <para>
/// Extracted so <see cref="LobbyCommandRunner"/> (request path) and <see cref="WorkerTransaction"/> (background
/// path) cannot drift apart on it. They retry for different reasons but must agree on <em>what counts as
/// contention</em>: if one of them silently stopped treating a row-version conflict as retryable, the symptom would
/// be a 500 or a lost ticket under concurrency — the exact class of bug this module is built around, and one that
/// a duplicated predicate is uniquely good at hiding.
/// </para>
/// </summary>
internal static class PostgresContention
{
    /// <summary>
    /// <see cref="DbUpdateConcurrencyException"/> is the one that actually catches concurrent last-seat joins.
    /// Every join bumps the lobby's <c>Revision</c>, so both racers issue <c>UPDATE … WHERE xmin = @loaded</c> and
    /// the loser affects zero rows. <strong>No unique index does this work</strong> —
    /// <c>ux_lobby_members_one_joined_per_user</c> is keyed on <c>UserId</c>, so it cheerfully admits two
    /// <em>different</em> users into the same final seat.
    /// </summary>
    public static bool IsContention(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException due => due.InnerException is PostgresException pg && IsContentionSqlState(pg.SqlState),
        PostgresException pg => IsContentionSqlState(pg.SqlState),
        _ => false,
    };

    private static bool IsContentionSqlState(string sqlState) =>
        sqlState is PostgresErrorCodes.UniqueViolation      // 23505 — a filtered unique index rejected the write
            or PostgresErrorCodes.SerializationFailure      // 40001
            or PostgresErrorCodes.DeadlockDetected;         // 40P01
}
