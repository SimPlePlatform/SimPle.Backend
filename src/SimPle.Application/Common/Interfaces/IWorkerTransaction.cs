namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// One background-worker unit of work: a single transaction, retried as a whole on contention.
///
/// <para>
/// This is <see cref="ILobbyCommandRunner"/>'s sibling, minus the advisory lock. That difference is deliberate and
/// is the reason there are two: the command runner serializes <em>one actor's</em> seat-acquiring commands against
/// each other, which is meaningless for a worker — a worker has no actor, and taking a lock keyed on some arbitrary
/// ticket's owner would serialize unrelated workers for no benefit. What the worker needs from a transaction is the
/// other property: <c>FOR UPDATE SKIP LOCKED</c> row locks are transaction-scoped, so they must be taken and
/// released inside one, and a crash mid-cycle must roll the claims back rather than strand them.
/// </para>
///
/// <para>
/// Contention is retried rather than surfaced. A worker has nobody to report a conflict to, and the losing side of
/// a claim race has nothing to apologise for — the tickets it failed to claim are still <c>Queued</c> and the next
/// cycle will see them.
/// </para>
/// </summary>
public interface IWorkerTransaction
{
    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction. On contention the whole delegate is re-run — including
    /// its reads — after the change tracker is cleared, so the rerun genuinely re-reads rather than re-deciding
    /// against the losing attempt's stale entities.
    ///
    /// If the retry budget is exhausted the exception propagates: the caller is a background loop, and the honest
    /// response to persistent contention there is to log the cycle as failed and try again on the next tick, not to
    /// invent a result.
    /// </summary>
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
}
