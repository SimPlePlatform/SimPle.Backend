using SimPle.Shared.Common;

namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Runs one lobby command as a bounded, retried, whole-transaction unit of work (reconciliation <strong>R3</strong>).
///
/// <para>
/// The existing <c>PostgresRetry.SaveChangesAsync</c> is not sufficient here and is deliberately left untouched.
/// It re-issues only the <em>save</em>, on <c>40001</c>/<c>40P01</c> only. A last-seat join that loses the capacity
/// race must instead <em>re-read</em> the lobby to discover it is now full and answer a typed <c>Lobbies.Full</c>;
/// replaying just the save would re-commit a decision made against state that is now stale. The brief (Risk #5) is
/// explicit: the application reruns the entire bounded transaction <em>including the read and decision logic</em>,
/// then surfaces a typed conflict — never a 500.
/// </para>
///
/// <para>
/// Three distinct failures mean "someone beat you; look again", and all three are retried:
/// <list type="bullet">
///   <item><c>23505</c> unique violation — e.g. <c>ux_lobby_members_one_joined_per_user</c> rejecting a second seat.</item>
///   <item><c>40001</c>/<c>40P01</c> serialization failure / deadlock.</item>
///   <item>A row-version (<c>xmin</c>) mismatch on the lobby, surfaced by EF as
///     <c>DbUpdateConcurrencyException</c>. This is the one that actually catches concurrent last-seat joins:
///     every join bumps <c>Revision</c>, so two racers both <c>UPDATE … WHERE xmin = @loaded</c> and the loser
///     affects zero rows. <strong>No index does this work</strong> — the member index is keyed on
///     <c>UserId</c>, so it happily admits two <em>different</em> users into the same last seat.</item>
/// </list>
/// </para>
///
/// <para>
/// Between attempts the change tracker is cleared, so the rerun genuinely re-reads rather than re-deciding against
/// the losing attempt's stale entities. Once the budget is spent the runner returns a typed
/// <c>Lobbies.ConcurrencyConflict</c> — the exception never escapes as a 500.
/// </para>
/// </summary>
public interface ILobbyCommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> inside one transaction, retrying the whole delegate on contention.
    ///
    /// <paramref name="actorUserId"/> takes a transaction-scoped advisory lock keyed on the actor. That is what
    /// makes the cross-table "one active lobby <strong>or</strong> one active ticket" check real: two filtered
    /// unique indexes on different tables cannot see each other, so without serializing an actor's seat-acquiring
    /// commands against each other, a concurrent join and enqueue would both read "nothing active" and both commit
    /// (brief Risk #2). The lock is released by the transaction, so nothing can leak it.
    ///
    /// A <c>Result.Fail</c> returned by the command is a decision, not a fault: it commits (nothing was written)
    /// and is <em>not</em> retried. Only a contention <em>exception</em> triggers a rerun.
    /// </summary>
    Task<Result<T>> RunAsync<T>(
        Guid actorUserId,
        Func<CancellationToken, Task<Result<T>>> command,
        CancellationToken ct = default);
}
