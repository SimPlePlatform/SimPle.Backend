using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The non-generic adapter boundary — the exact shape Module 8 calls. Wraps one typed
/// <see cref="IGameDefinition{TState,TCommand,TPlayerView}"/> registration and translates between the typed
/// definition and the byte/envelope world M8 stores and transports.
/// <para>
/// An implementation deserializes only the concrete state/command/view types supplied by its own registered
/// typed definition. It never resolves a CLR type name from input, never uses reflection-based arbitrary
/// activation, and never falls back on an unknown type — a payload built for a different game definition is
/// rejected as a typed <see cref="EngineErrorCode.CorruptState"/>/<see cref="EngineErrorCode.InvalidCommandType"/>,
/// never silently accepted.
/// </para>
/// </summary>
public interface IHostedGameDefinition
{
    GameDefinitionMetadata Metadata { get; }

    /// <summary>
    /// Builds the opening envelope at revision 0 from one server-drawn 128-bit match seed. Never called with a
    /// caller-supplied seed — <see cref="Pcg32.FromMatchSeed"/> is the only place the seed is consumed.
    /// </summary>
    GameStateEnvelope CreateInitialState(GameSetup setup, UInt128 matchSeed, CancellationToken cancellationToken);

    /// <summary>
    /// Deserializes <paramref name="state"/>, applies <paramref name="command"/>, and re-serializes the result.
    /// Builds <see cref="CommandContext"/> itself from <paramref name="state"/>.Revision and the envelope's
    /// server-bound fields — a caller cannot pass a fabricated <c>CurrentRevision</c>, which is what makes the
    /// staleness check trustworthy.
    /// <para>
    /// Returns a rejection <see cref="EngineTransition"/> — never throws — for every input-shaped failure
    /// (corrupt checksum, unsupported schema, stale revision, illegal actor, invalid command type/shape). A
    /// definition-thrown exception during the typed call is the only case this rethrows; the caller
    /// (<see cref="GameHostInvoker"/>) maps it to <see cref="EngineErrorCode.PluginFailure"/> so no plugin
    /// exception text ever reaches a log or a client.
    /// </para>
    /// </summary>
    EngineTransition ApplyCommand(
        GameStateEnvelope state,
        GameCommandEnvelope command,
        CancellationToken cancellationToken);

    /// <summary>Deserializes <paramref name="state"/> and builds the redacted projection for one viewer.</summary>
    PlayerViewEnvelope ProjectView(GameStateEnvelope state, ViewerContext viewer, CancellationToken cancellationToken);

    /// <summary>Deserializes <paramref name="state"/> and asks the typed definition for its terminal verdict.</summary>
    TerminalResultCandidate? EvaluateResult(GameStateEnvelope state, CancellationToken cancellationToken);
}
