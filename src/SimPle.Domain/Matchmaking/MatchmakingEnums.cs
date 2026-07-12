namespace SimPle.Domain.Matchmaking;

/// <summary>
/// Ticket lifecycle: <c>Queued -&gt; Claimed -&gt; Matched|Requeued|Failed</c>, or
/// <c>Queued -&gt; Cancelled|TimedOut</c>.
///
/// <c>Requeued</c> is a transient bookkeeping state that immediately returns to <c>Queued</c> (a failed M8 handoff
/// retries before the original deadline); <c>Matched</c>, <c>Failed</c>, <c>Cancelled</c>, and <c>TimedOut</c> are
/// terminal.
/// </summary>
public enum MatchmakingTicketState
{
    Queued,
    Claimed,
    Matched,
    Requeued,
    Failed,
    Cancelled,
    TimedOut,
}

/// <summary>
/// <c>Active</c> is the only state the partial unique index counts. A superseded or failed assignment frees the
/// ticket for a fresh one without ever permitting two live assignments (Risk #1).
/// </summary>
public enum MatchmakingAssignmentState
{
    Active,
    Superseded,
    Failed,
}

/// <summary>Expected domain outcomes of a ticket mutation. Same rationale as <c>LobbyOutcome</c>.</summary>
public enum MatchmakingOutcome
{
    Ok,

    /// <summary>Ticket is already terminal. → <c>Matchmaking.TicketExpired</c> / current status.</summary>
    Terminal,

    /// <summary>Past the absolute 60-second deadline. → <c>Matchmaking.TicketExpired</c></summary>
    Expired,

    /// <summary>
    /// A cancel arrived after a worker claim. This is deliberately <em>not</em> an error: the caller returns the
    /// ticket's current status with HTTP 200 (<c>Matchmaking.CancelTooLate</c> is a 200, per the error catalogue).
    /// </summary>
    AlreadyClaimed,

    /// <summary>The transition is illegal from the ticket's current state.</summary>
    InvalidTransition,

    /// <summary>No retry budget remains for another M8 handoff attempt.</summary>
    RetryBudgetExhausted,
}
