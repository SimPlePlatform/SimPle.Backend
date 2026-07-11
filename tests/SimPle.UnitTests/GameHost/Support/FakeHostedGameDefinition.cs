using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.Support;

/// <summary>
/// A configurable <see cref="IHostedGameDefinition"/> test double for exercising <see cref="GameHostInvoker"/>
/// and <see cref="GameRegistry"/> in isolation from any real typed engine — each method delegates to an
/// injectable callback so a test can simulate a slow, throwing, or oversized-output definition without
/// building a second reference engine.
/// </summary>
internal sealed class FakeHostedGameDefinition : IHostedGameDefinition
{
    public GameDefinitionMetadata Metadata { get; }

    public Func<GameSetup, UInt128, CancellationToken, GameStateEnvelope>? OnCreateInitialState { get; set; }
    public Func<GameStateEnvelope, GameCommandEnvelope, CancellationToken, EngineTransition>? OnApplyCommand { get; set; }
    public Func<GameStateEnvelope, ViewerContext, CancellationToken, PlayerViewEnvelope>? OnProjectView { get; set; }
    public Func<GameStateEnvelope, CancellationToken, TerminalResultCandidate?>? OnEvaluateResult { get; set; }

    public FakeHostedGameDefinition(GameDefinitionMetadata metadata) => Metadata = metadata;

    public GameStateEnvelope CreateInitialState(GameSetup setup, UInt128 matchSeed, CancellationToken cancellationToken) =>
        OnCreateInitialState is not null
            ? OnCreateInitialState(setup, matchSeed, cancellationToken)
            : throw new NotSupportedException($"{nameof(OnCreateInitialState)} was not configured for this test.");

    public EngineTransition ApplyCommand(GameStateEnvelope state, GameCommandEnvelope command, CancellationToken cancellationToken) =>
        OnApplyCommand is not null
            ? OnApplyCommand(state, command, cancellationToken)
            : throw new NotSupportedException($"{nameof(OnApplyCommand)} was not configured for this test.");

    public PlayerViewEnvelope ProjectView(GameStateEnvelope state, ViewerContext viewer, CancellationToken cancellationToken) =>
        OnProjectView is not null
            ? OnProjectView(state, viewer, cancellationToken)
            : throw new NotSupportedException($"{nameof(OnProjectView)} was not configured for this test.");

    public TerminalResultCandidate? EvaluateResult(GameStateEnvelope state, CancellationToken cancellationToken) =>
        OnEvaluateResult is not null
            ? OnEvaluateResult(state, cancellationToken)
            : throw new NotSupportedException($"{nameof(OnEvaluateResult)} was not configured for this test.");
}
