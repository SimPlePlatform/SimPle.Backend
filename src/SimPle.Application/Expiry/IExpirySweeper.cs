namespace SimPle.Application.Expiry;

/// <summary>
/// What one expiry sweep did. <paramref name="MaxTicketLag"/> is how far past its deadline the most overdue ticket
/// in this batch was — the module's <c>expiry lag</c> signal, and the number the benchmark holds under 5 seconds.
/// It is measured, not assumed: a sweep that silently fell behind would otherwise look identical to one that had
/// nothing to do.
/// </summary>
public sealed record ExpirySweepResult(
    int TicketsExpired,
    int LobbiesExpired,
    int InvitesExpired,
    TimeSpan MaxTicketLag)
{
    public static readonly ExpirySweepResult Empty = new(0, 0, 0, TimeSpan.Zero);

    public int Total => TicketsExpired + LobbiesExpired + InvitesExpired;
}

/// <summary>
/// Sweeps tickets past their 60-second deadline, lobbies past their 2-hour lifetime, and invites past their
/// 30-minute lifetime.
///
/// <para>
/// Unlike matching, the sweep runs <strong>with or without Module 8</strong>. That is the honest half of the queue:
/// a player who enqueues before a match runtime exists still watches their band widen and still gets a truthful
/// <c>TimedOut</c> at sixty seconds, instead of a ticket that sits <c>Queued</c> forever because the only thing
/// that could have resolved it does not exist yet.
/// </para>
///
/// <para>
/// Every transition it drives is idempotent at the domain level (<c>TryTimeOut</c> / <c>TryExpire</c> return false
/// rather than transitioning twice), so a sweep that overlaps with a previous run, or re-runs after a crash, cannot
/// double-expire anything.
/// </para>
///
/// <para>
/// Join credentials are deliberately <em>not</em> swept. A credential is already dead the moment it is past its
/// deadline — <c>LobbyJoinCredential.CanRedeem</c> checks the clock, and the join path calls it — so a sweep would
/// change nothing a caller can observe. Rewriting its state row would be bookkeeping that buys no behavior, and the
/// row is retained as the audit trail of which generation was live when.
/// </para>
/// </summary>
public interface IExpirySweeper
{
    Task<ExpirySweepResult> SweepAsync(CancellationToken ct = default);
}
