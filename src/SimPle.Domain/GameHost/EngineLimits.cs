namespace SimPle.Domain.GameHost;

/// <summary>
/// Hard host-boundary defaults. These caps protect the host call boundary, not Module 8's durable storage —
/// if M8 relaxes a column or transport limit the effective cap diverges, so the two must stay aligned.
/// Changing any value here requires benchmark and security evidence plus an ADR; the values are pinned by
/// <c>EngineLimitsTests</c> so a silent change fails the build.
/// </summary>
public static class EngineLimits
{
    public const int MaxCommandPayloadBytes = 16 * 1024;
    public const int MaxSerializedStateBytes = 256 * 1024;
    public const int MaxPlayerViewBytes = 256 * 1024;
    public const int MaxGameEventBatchBytes = 64 * 1024;

    public const int MinPlayers = 1;
    public const int MaxPlayers = 8;

    public const int MaxEmittedEventsPerCommand = 128;

    /// <summary>Target for a single non-AI command. Exceeding it is a benchmark failure, not a runtime error.</summary>
    public static readonly TimeSpan SoftExecutionBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>The host requests cooperative cancellation once a call has run this long.</summary>
    public static readonly TimeSpan CancellationRequestThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Grace period a cooperative definition gets to observe its token and return after cancellation is
    /// requested. Output arriving later is discarded and mapped to <see cref="EngineErrorCode.ExecutionBudgetExceeded"/>.
    /// In-process code that ignores its token cannot be safely preempted: that is a release blocker, not a
    /// runtime-recoverable condition.
    /// </summary>
    public static readonly TimeSpan CooperativeReturnGrace = TimeSpan.FromMilliseconds(50);
}
