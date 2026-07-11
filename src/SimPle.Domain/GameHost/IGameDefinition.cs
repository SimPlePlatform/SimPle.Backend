namespace SimPle.Domain.GameHost;

/// <summary>
/// The one contract a Phase 2 game implements. A definition is <b>pure game rules</b> and nothing else: it has
/// no HTTP, SignalR, EF Core, or app-shell dependency, and the platform can host it without knowing anything
/// about how it is played.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism is the load-bearing property.</b> For a completed call, identical inputs must produce
/// byte-identical output — that is what makes replay, golden vectors, and dispute resolution possible. An
/// implementation therefore must not read the system clock, touch the network, database, filesystem, or
/// environment, hold static mutable state, iterate a collection whose order is not defined (a
/// <see cref="System.Collections.Generic.HashSet{T}"/> or <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
/// without an explicit sort), or use any randomness other than the supplied <see cref="Pcg32"/>. This cannot be
/// enforced by the type system: it is guaranteed by code review and by the two-process golden-hash comparison,
/// and a violation is a release blocker.
/// </para>
/// <para>
/// <b>Purity, literally.</b> Methods must not mutate the <c>state</c> they are given — they return a new one.
/// The host may reuse an input state instance across calls (for example when re-projecting a view per seat),
/// so in-place mutation corrupts other callers.
/// </para>
/// <para>
/// <b>Cancellation is cooperative.</b> Definitions receive a token and must check it at every bounded loop.
/// Trusted in-process code cannot be hard-preempted, so a definition that ignores its token cannot be stopped
/// by the host; the execution budget in <see cref="EngineLimits"/> only lets the host discard the late result.
/// </para>
/// <para>
/// <b>Authority is not yours.</b> <typeparamref name="TCommand"/> is client-supplied and untrusted. The actor
/// and seat come from <see cref="CommandContext"/>, which the server binds from authenticated membership. Never
/// read an identity out of the command payload.
/// </para>
/// </remarks>
/// <typeparam name="TState">The authoritative state. JSON-serializable, immutable by convention. Holds hidden information.</typeparam>
/// <typeparam name="TCommand">The command union. Members are allow-listed by stable string discriminator, never by CLR type name.</typeparam>
/// <typeparam name="TPlayerView">The redacted, per-viewer projection. This is the only thing a client ever sees.</typeparam>
public interface IGameDefinition<TState, TCommand, TPlayerView>
    where TState : class
    where TCommand : class
    where TPlayerView : class
{
    /// <summary>Immutable identity and capabilities. The <c>(Slug, EngineVersion)</c> pair is the registry key.</summary>
    GameDefinitionMetadata Metadata { get; }

    /// <summary>
    /// Builds the opening state. All randomness (a shuffled deck, a starting layout) must be drawn from
    /// <paramref name="rng"/>, so the same match seed always deals the same game.
    /// </summary>
    TState CreateInitialState(GameSetup setup, Pcg32 rng, CancellationToken cancellationToken);

    /// <summary>
    /// Validates and applies one command, returning either a new state or a typed rejection. Must not mutate
    /// <paramref name="state"/>.
    /// <para>
    /// Draw from <paramref name="rng"/> only on a path that ends in
    /// <see cref="EngineDecision{TState}.Accept"/>: the host discards the advanced stream on rejection, so a
    /// draw taken before a rejection is silently thrown away and will be re-taken by the next command —
    /// which is correct, but only if the definition does not also assume the draw "happened".
    /// </para>
    /// </summary>
    EngineDecision<TState> ApplyCommand(
        TState state,
        TCommand command,
        CommandContext context,
        Pcg32 rng,
        CancellationToken cancellationToken);

    /// <summary>
    /// Projects the state for one viewer. This is the redaction boundary: whatever is not put into the
    /// projection cannot leak, and a spectator must be given strictly less than any seated player.
    /// The RNG state and the raw seed must never appear in a projection.
    /// </summary>
    TPlayerView ProjectView(TState state, ViewerContext viewer, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the terminal result when the rules consider the state finished, or <see langword="null"/> while
    /// the match is still in progress. Pure, so asking twice returns the same candidate.
    /// </summary>
    TerminalResultCandidate? EvaluateResult(TState state, CancellationToken cancellationToken);
}
