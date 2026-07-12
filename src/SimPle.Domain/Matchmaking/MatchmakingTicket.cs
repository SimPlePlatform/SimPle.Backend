using SimPle.Domain.Common;

namespace SimPle.Domain.Matchmaking;

/// <summary>
/// One user's place in the Quick Match queue. The ticket <em>snapshots</em> everything the candidate pool keys on,
/// so a lobby setting or profile change mid-queue can never silently re-pool a waiting ticket.
///
/// Rating is a snapshot for the same reason — and, until M10 exists, it is honestly provisional: every user is
/// 1200 with <see cref="RatingSourceVersion"/> = <c>provisional-1200-v1</c>. The legacy global <c>User.Elo</c>
/// column is deliberately <em>not</em> substituted: it is a single cross-game number, so presenting it as a
/// per-game rating would be a fabricated signal.
/// </summary>
public class MatchmakingTicket : Entity
{
    /// <summary>The only rating source before M10. Recorded on every ticket so the provenance is auditable.</summary>
    public const string ProvisionalRatingSource = "provisional-1200-v1";
    public const int ProvisionalRating = 1200;

    /// <summary>Attempts to hand a claimed ticket to M8 before it is declared Failed.</summary>
    public const int DefaultRetryBudget = 3;

    public Guid UserId { get; private set; }

    // ── Candidate-pool key: every field below must match exactly for two tickets to be poolable ──
    public string GameSlug { get; private set; } = default!;
    public int CapabilityVersion { get; private set; }
    public string Mode { get; private set; } = default!;
    public int PlayerCount { get; private set; }
    public string TimeControlId { get; private set; } = default!;
    public bool Rated { get; private set; }
    public string ResolvedRegion { get; private set; } = default!;

    public int Rating { get; private set; }
    public string RatingSourceVersion { get; private set; } = default!;

    public MatchmakingTicketState State { get; private set; } = MatchmakingTicketState.Queued;

    public DateTime EnqueuedAtUtc { get; private set; }

    /// <summary>Absolute, set once at enqueue. A requeue retries <em>before</em> this instant; it never extends it.</summary>
    public DateTime DeadlineAtUtc { get; private set; }

    public int RetryBudget { get; private set; }

    /// <summary>Identifies the worker holding the current claim. Cleared on requeue.</summary>
    public string? ClaimedByWorker { get; private set; }

    public DateTime? ClaimedAtUtc { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }
    public Guid CorrelationId { get; private set; }

    /// <summary>Mapped to xmin via IsRowVersion() in EF config.</summary>
    public uint Version { get; private set; }

    private MatchmakingTicket() { }

    public static MatchmakingTicket Enqueue(
        Guid userId,
        string gameSlug,
        int capabilityVersion,
        string mode,
        int playerCount,
        string timeControlId,
        bool rated,
        string resolvedRegion,
        int rating,
        string ratingSourceVersion,
        Guid correlationId,
        DateTime nowUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId must not be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(gameSlug))
            throw new ArgumentException("GameSlug must not be empty.", nameof(gameSlug));
        if (capabilityVersion < 1)
            throw new ArgumentException("CapabilityVersion must be at least 1.", nameof(capabilityVersion));
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("Mode must not be empty.", nameof(mode));
        if (playerCount < 2)
            throw new ArgumentException("PlayerCount must be at least 2 — Quick Match is a multiplayer queue.", nameof(playerCount));
        if (string.IsNullOrWhiteSpace(ratingSourceVersion))
            throw new ArgumentException("RatingSourceVersion must not be empty.", nameof(ratingSourceVersion));
        if (!SimPle.Domain.Lobbies.LobbyRegion.IsResolved(resolvedRegion))
            throw new ArgumentException($"ResolvedRegion '{resolvedRegion}' must be an explicit allow-listed region, never 'Auto'.", nameof(resolvedRegion));

        return new MatchmakingTicket
        {
            UserId = userId,
            GameSlug = gameSlug,
            CapabilityVersion = capabilityVersion,
            Mode = mode,
            PlayerCount = playerCount,
            TimeControlId = timeControlId,
            Rated = rated,
            ResolvedRegion = resolvedRegion,
            Rating = rating,
            RatingSourceVersion = ratingSourceVersion,
            State = MatchmakingTicketState.Queued,
            EnqueuedAtUtc = nowUtc,
            DeadlineAtUtc = nowUtc + MatchmakingBands.Deadline,
            RetryBudget = DefaultRetryBudget,
            CorrelationId = correlationId,
        };
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public bool IsNonTerminal =>
        State is MatchmakingTicketState.Queued or MatchmakingTicketState.Claimed or MatchmakingTicketState.Requeued;

    public TimeSpan AgeAt(DateTime nowUtc) => nowUtc - EnqueuedAtUtc;

    public bool IsExpired(DateTime nowUtc) => nowUtc >= DeadlineAtUtc;

    /// <summary>The ticket's current rating-band half-width, or null once it has reached its deadline.</summary>
    public int? CurrentBand(DateTime nowUtc) => MatchmakingBands.BandFor(AgeAt(nowUtc));

    /// <summary>
    /// The rating window this ticket will accept right now. A group is compatible only when its rating range fits
    /// <em>every</em> member's window — not just the anchor's (6C enforces that).
    /// </summary>
    public (int Low, int High)? RatingWindow(DateTime nowUtc)
    {
        var band = CurrentBand(nowUtc);
        return band is null ? null : (Rating - band.Value, Rating + band.Value);
    }

    // ── Transitions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Queued -&gt; Claimed, by a worker that won the <c>FOR UPDATE SKIP LOCKED</c> row lock. The lock prevents two
    /// workers <em>contending</em>; it is not exclusivity (Risk #1) — only the partial unique index on active
    /// assignment makes double-assignment impossible.
    /// </summary>
    public MatchmakingOutcome Claim(string workerId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("WorkerId must not be empty.", nameof(workerId));

        if (State is not (MatchmakingTicketState.Queued or MatchmakingTicketState.Requeued))
            return State == MatchmakingTicketState.Claimed
                ? MatchmakingOutcome.AlreadyClaimed
                : MatchmakingOutcome.Terminal;

        // A worker must not claim a ticket that has already run out the clock; the expiry sweep owns it now.
        if (IsExpired(nowUtc)) return MatchmakingOutcome.Expired;

        State = MatchmakingTicketState.Claimed;
        ClaimedByWorker = workerId;
        ClaimedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    /// <summary>Claimed -&gt; Matched. Terminal: an assignment plus one MatchRequestedV1 committed together.</summary>
    public MatchmakingOutcome MarkMatched(DateTime nowUtc)
    {
        if (State != MatchmakingTicketState.Claimed) return MatchmakingOutcome.InvalidTransition;

        State = MatchmakingTicketState.Matched;
        ResolvedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    /// <summary>
    /// Claimed -&gt; Queued, on a failed M8 handoff. Spends one unit of retry budget and returns the ticket to the
    /// pool <em>under its original deadline</em> — a requeue never buys more time. With no budget left the ticket
    /// becomes <see cref="MatchmakingTicketState.Failed"/> instead.
    /// </summary>
    public MatchmakingOutcome Requeue(DateTime nowUtc)
    {
        if (State != MatchmakingTicketState.Claimed) return MatchmakingOutcome.InvalidTransition;

        // Terminal states keep ClaimedByWorker: it is the attribution behind the matchmaking-worker-failure signal.
        // Only a return to Queued clears it, because a queued ticket naming a worker would mean a claim leaked.
        if (RetryBudget <= 0)
        {
            State = MatchmakingTicketState.Failed;
            ResolvedAtUtc = nowUtc;
            Touch();
            return MatchmakingOutcome.RetryBudgetExhausted;
        }

        // Past its deadline there is nothing to retry into.
        if (IsExpired(nowUtc))
        {
            State = MatchmakingTicketState.TimedOut;
            ResolvedAtUtc = nowUtc;
            Touch();
            return MatchmakingOutcome.Expired;
        }

        RetryBudget -= 1;
        State = MatchmakingTicketState.Queued;
        ClaimedByWorker = null;
        ClaimedAtUtc = null;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    /// <summary>Claimed -&gt; Failed, terminal, when the handoff cannot be retried at all.</summary>
    public MatchmakingOutcome MarkFailed(DateTime nowUtc)
    {
        if (State != MatchmakingTicketState.Claimed) return MatchmakingOutcome.InvalidTransition;

        State = MatchmakingTicketState.Failed;
        ResolvedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    /// <summary>
    /// User-initiated cancel. Commits <em>only</em> while Queued: once a worker has claimed the ticket, the cancel
    /// is too late and the caller returns the ticket's current status with a 200, not an error.
    /// </summary>
    public MatchmakingOutcome Cancel(DateTime nowUtc)
    {
        if (State == MatchmakingTicketState.Claimed) return MatchmakingOutcome.AlreadyClaimed;
        if (State != MatchmakingTicketState.Queued) return MatchmakingOutcome.Terminal;

        State = MatchmakingTicketState.Cancelled;
        ResolvedAtUtc = nowUtc;
        Touch();
        return MatchmakingOutcome.Ok;
    }

    /// <summary>
    /// Expiry sweep entry point (6C's expiry worker). Idempotent: a re-run over an already-terminal or not-yet-due
    /// ticket is a no-op rather than a second state change.
    /// </summary>
    public bool TryTimeOut(DateTime nowUtc)
    {
        if (!IsNonTerminal || !IsExpired(nowUtc)) return false;

        State = MatchmakingTicketState.TimedOut;
        ResolvedAtUtc = nowUtc;
        Touch();
        return true;
    }
}
