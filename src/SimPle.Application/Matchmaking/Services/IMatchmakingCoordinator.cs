namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// What one matching cycle did. Every field is a count or a duration — never an identifier — so it can be logged
/// and turned into the module's <c>matchmaking-queue-age</c> / <c>matchmaking-claim-conflict</c> /
/// <c>matchmaking-worker-failure</c> signals without leaking who is in the queue.
/// </summary>
public sealed record MatchmakingCycleResult(
    bool Executed,
    int TicketsClaimed,
    int ProposalsFormed,
    int TicketsMatched,
    TimeSpan? OldestQueuedAge)
{
    /// <summary>
    /// The cycle did not run because no match runtime is registered. This is the normal state before Module 8 —
    /// not a failure, and not something to alert on.
    /// </summary>
    public static MatchmakingCycleResult Disabled(TimeSpan? oldestQueuedAge = null) =>
        new(Executed: false, 0, 0, 0, oldestQueuedAge);

    public static readonly MatchmakingCycleResult Idle =
        new(Executed: true, 0, 0, 0, null);
}

/// <summary>
/// One iteration of the matching loop, as an application service rather than as code buried in a
/// <c>BackgroundService</c>.
///
/// That split is what makes the module's hardest guarantee testable: "two competing workers produce zero duplicate
/// assignment" is asserted by running <em>two coordinators concurrently against real PostgreSQL</em>, which is only
/// possible because a cycle is a callable method rather than a timer tick inside a hosted service.
/// </summary>
public interface IMatchmakingCoordinator
{
    /// <summary>
    /// Claims a bounded batch, forms proposals, and commits each one's assignments plus its single
    /// <c>MatchRequestedV1</c> — all in one transaction.
    ///
    /// <paramref name="workerId"/> is recorded on every ticket it claims. It survives onto terminal rows on
    /// purpose: that attribution is what backs the <c>matchmaking-worker-failure</c> signal, so a worker that
    /// consistently loses its handoffs can be identified without correlating logs.
    /// </summary>
    Task<MatchmakingCycleResult> RunCycleAsync(string workerId, CancellationToken ct = default);
}
