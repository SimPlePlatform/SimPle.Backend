namespace SimPle.Domain.GameHost;

/// <summary>
/// The non-generic result of one hosted command — the exact shape Module 8 will consume. Produced by the host
/// adapter from a typed <see cref="EngineDecision{TState}"/>.
/// <para>
/// On rejection, <see cref="NextState"/> is null, no events are carried, and <see cref="NextRevision"/> equals
/// <see cref="PriorRevision"/>: a failed command advances neither the revision nor the RNG, so retrying it or
/// replaying the log produces the same result.
/// </para>
/// </summary>
public sealed class EngineTransition
{
    public bool Accepted { get; }

    public int PriorRevision { get; }
    public int NextRevision { get; }

    /// <summary>Non-null exactly when <see cref="Accepted"/> is true.</summary>
    public GameStateEnvelope? NextState { get; }

    /// <summary>Stable <c>Engine.*</c> code, or <see langword="null"/> when accepted.</summary>
    public string? RejectionCode { get; }

    /// <summary>Client-safe detail. Never hidden state, seed material, or exception text.</summary>
    public string? RejectionDetail { get; }

    public IReadOnlyList<GameEvent> PublicEvents { get; }
    public IReadOnlyList<GameEvent> PrivateEvents { get; }

    public TerminalResultCandidate? TerminalResult { get; }
    public EngineState EngineState { get; }

    private EngineTransition(
        bool accepted,
        int priorRevision,
        int nextRevision,
        GameStateEnvelope? nextState,
        string? rejectionCode,
        string? rejectionDetail,
        IReadOnlyList<GameEvent> publicEvents,
        IReadOnlyList<GameEvent> privateEvents,
        TerminalResultCandidate? terminalResult,
        EngineState engineState)
    {
        Accepted = accepted;
        PriorRevision = priorRevision;
        NextRevision = nextRevision;
        NextState = nextState;
        RejectionCode = rejectionCode;
        RejectionDetail = rejectionDetail;
        PublicEvents = publicEvents;
        PrivateEvents = privateEvents;
        TerminalResult = terminalResult;
        EngineState = engineState;
    }

    public static EngineTransition Accept(
        int priorRevision,
        GameStateEnvelope nextState,
        IEnumerable<GameEvent> events,
        TerminalResultCandidate? terminalResult)
    {
        ArgumentNullException.ThrowIfNull(nextState);

        if (nextState.Revision != priorRevision + 1)
        {
            throw new ArgumentException(
                $"An accepted command must advance the revision by exactly one (prior {priorRevision}, next {nextState.Revision}).",
                nameof(nextState));
        }

        var all = events.ToList();
        if (all.Count > EngineLimits.MaxEmittedEventsPerCommand)
        {
            throw new ArgumentOutOfRangeException(
                nameof(events),
                all.Count,
                $"A command may emit at most {EngineLimits.MaxEmittedEventsPerCommand} events.");
        }

        return new EngineTransition(
            accepted: true,
            priorRevision: priorRevision,
            nextRevision: nextState.Revision,
            nextState: nextState,
            rejectionCode: null,
            rejectionDetail: null,
            publicEvents: all.Where(e => e.IsPublic).ToList(),
            privateEvents: all.Where(e => !e.IsPublic).ToList(),
            terminalResult: terminalResult,
            engineState: terminalResult is null ? EngineState.InProgress : EngineState.Terminal);
    }

    public static EngineTransition Reject(int priorRevision, EngineErrorCode code, string? detail = null) =>
        new(
            accepted: false,
            priorRevision: priorRevision,
            nextRevision: priorRevision,
            nextState: null,
            rejectionCode: code.ToStableCode(),
            rejectionDetail: detail,
            publicEvents: [],
            privateEvents: [],
            terminalResult: null,
            engineState: EngineState.InProgress);
}
