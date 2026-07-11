namespace SimPle.Domain.GameHost;

/// <summary>
/// What a <b>typed</b> definition returns from <c>ApplyCommand</c>, in its own state type. The host adapter
/// serializes this into the non-generic <see cref="EngineTransition"/> that Module 8 consumes; a definition
/// never touches bytes, envelopes, or checksums.
/// <para>
/// A rejection carries <b>no next state</b> and <b>no events</b>. That is enforced in <see cref="Reject"/>
/// rather than left to the definition author, so a buggy engine cannot leak a partially-mutated board or a
/// hidden card through a failed move.
/// </para>
/// </summary>
public sealed class EngineDecision<TState> where TState : class
{
    public bool Accepted { get; }

    /// <summary>The state after the command. Non-null exactly when <see cref="Accepted"/> is true.</summary>
    public TState? NextState { get; }

    public EngineErrorCode? RejectionCode { get; }

    /// <summary>Client-safe detail. Must never contain hidden state, seed material, or exception text.</summary>
    public string? RejectionDetail { get; }

    public IReadOnlyList<GameEvent> Events { get; }

    /// <summary>Set when <see cref="EngineState"/> is <see cref="GameHost.EngineState.Terminal"/>.</summary>
    public TerminalResultCandidate? TerminalResult { get; }

    public EngineState EngineState { get; }

    private EngineDecision(
        bool accepted,
        TState? nextState,
        EngineErrorCode? rejectionCode,
        string? rejectionDetail,
        IReadOnlyList<GameEvent> events,
        TerminalResultCandidate? terminalResult,
        EngineState engineState)
    {
        Accepted = accepted;
        NextState = nextState;
        RejectionCode = rejectionCode;
        RejectionDetail = rejectionDetail;
        Events = events;
        TerminalResult = terminalResult;
        EngineState = engineState;
    }

    public static EngineDecision<TState> Accept(
        TState nextState,
        IEnumerable<GameEvent>? events = null,
        TerminalResultCandidate? terminalResult = null)
    {
        ArgumentNullException.ThrowIfNull(nextState);

        var emitted = events?.ToList() ?? [];
        if (emitted.Count > EngineLimits.MaxEmittedEventsPerCommand)
        {
            throw new ArgumentOutOfRangeException(
                nameof(events),
                emitted.Count,
                $"A command may emit at most {EngineLimits.MaxEmittedEventsPerCommand} events.");
        }

        return new EngineDecision<TState>(
            accepted: true,
            nextState: nextState,
            rejectionCode: null,
            rejectionDetail: null,
            events: emitted,
            terminalResult: terminalResult,
            engineState: terminalResult is null ? EngineState.InProgress : EngineState.Terminal);
    }

    /// <summary>
    /// Rejects the command. The revision and the RNG stream are left untouched by the host, so a rejected
    /// command is indistinguishable from one that was never issued.
    /// </summary>
    public static EngineDecision<TState> Reject(EngineErrorCode code, string? detail = null) =>
        new(
            accepted: false,
            nextState: null,
            rejectionCode: code,
            rejectionDetail: detail,
            events: [],
            terminalResult: null,
            engineState: EngineState.InProgress);
}
