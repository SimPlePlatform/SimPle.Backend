using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The safe host boundary — the exact shape Module 8 calls. Resolves the target engine from the registry,
/// enforces every <see cref="EngineLimits"/> size and timing budget before and after the underlying
/// <see cref="IHostedGameDefinition"/> call, and translates a plugin exception or a timeout into a stable
/// <see cref="EngineErrorCode"/> that never carries hidden state, seed material, or raw exception text.
/// </summary>
public interface IGameHostInvoker
{
    GameHostResult<GameStateEnvelope> CreateMatch(
        string gameSlug,
        int engineVersion,
        GameSetup setup,
        UInt128 matchSeed,
        CancellationToken cancellationToken);

    /// <summary>
    /// Always returns an <see cref="EngineTransition"/> — never throws for an input-shaped or host-boundary
    /// failure — because a transition already has a prior revision to report even on rejection.
    /// </summary>
    EngineTransition ApplyCommand(GameStateEnvelope state, GameCommandEnvelope command, CancellationToken cancellationToken);

    GameHostResult<PlayerViewEnvelope> ProjectView(GameStateEnvelope state, ViewerContext viewer, CancellationToken cancellationToken);

    GameHostResult<TerminalResultCandidate?> EvaluateResult(GameStateEnvelope state, CancellationToken cancellationToken);
}
