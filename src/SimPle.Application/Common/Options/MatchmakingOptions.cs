namespace SimPle.Application.Common.Options;

/// <summary>
/// The matching worker's loop and batch shape (slice 6C).
///
/// None of these are secrets, and all have working defaults — the module runs correctly with no configuration at
/// all. They exist so a deployment can widen the batch or slow the loop without a rebuild.
/// </summary>
public sealed class MatchmakingOptions
{
    public const string SectionName = "Matchmaking";

    /// <summary>
    /// How often a worker attempts a cycle. Two seconds matches the client's ticket poll: a shorter loop would make
    /// the queue no faster from the player's side, since they cannot learn about a match until their next poll.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tickets claimed per cycle. Bounded on purpose: an unbounded claim would let one worker take the whole queue
    /// and turn a second worker into a spectator, and would make a single failed cycle roll back everything.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Whether the matching worker is hosted at all. Distinct from the M8 readiness gate — that gate is the
    /// <em>correctness</em> boundary (the cycle refuses to run without a match runtime, no matter what this says);
    /// this is an operational switch for the rollback plan, which calls for disabling the workers while preserving
    /// every lobby and ticket record.
    /// </summary>
    public bool WorkerEnabled { get; set; } = true;
}

/// <summary>The expiry sweep's loop and batch shape. The sweep runs with or without Module 8 — see IExpirySweeper.</summary>
public sealed class ExpiryOptions
{
    public const string SectionName = "Expiry";

    /// <summary>
    /// Comfortably inside the benchmark's 5-second expiry-lag budget, so a ticket's honest <c>TimedOut</c> lands
    /// within a poll or two of its actual deadline rather than whenever the sweep next happens to wake.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows of each kind (tickets, lobbies, invites) per sweep.</summary>
    public int BatchSize { get; set; } = 200;

    public bool WorkerEnabled { get; set; } = true;
}

/// <summary>The outbox dispatcher's loop, batch, lease, and retry budget (D3).</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long a leased delivery is considered in-flight. It must comfortably exceed the slowest handler, because
    /// a lease that expires while its handler is still working invites a second dispatcher to run the same event
    /// concurrently — survivable (handlers are idempotent) but wasteful, and it makes the attempt count lie.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Attempts before a delivery is dead-lettered. Bounded so that one permanently-broken handler cannot retry
    /// forever and crowd out every other event in the batch.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    public bool WorkerEnabled { get; set; } = true;
}
