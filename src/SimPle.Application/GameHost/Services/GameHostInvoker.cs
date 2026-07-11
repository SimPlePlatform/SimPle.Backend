using Microsoft.Extensions.Logging;
using SimPle.Application.GameHost.Serialization;
using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The one <see cref="IGameHostInvoker"/> implementation. Every public method follows the same shape: resolve
/// the engine from <see cref="IGameRegistry"/>, check the inbound size budget, run the call under the
/// cooperative-cancellation watchdog, then check the outbound size budget.
/// </summary>
public sealed class GameHostInvoker : IGameHostInvoker
{
    private readonly IGameRegistry _registry;
    private readonly ILogger<GameHostInvoker> _logger;

    public GameHostInvoker(IGameRegistry registry, ILogger<GameHostInvoker> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public GameHostResult<GameStateEnvelope> CreateMatch(
        string gameSlug,
        int engineVersion,
        GameSetup setup,
        UInt128 matchSeed,
        CancellationToken cancellationToken)
    {
        if (!TryResolveOrFail<GameStateEnvelope>(gameSlug, engineVersion, out var definition, out var resolutionFailure))
            return resolutionFailure!;

        var result = Execute(ct => definition!.CreateInitialState(setup, matchSeed, ct), definition!.Metadata, nameof(CreateMatch), cancellationToken);
        if (!result.Succeeded)
            return result;

        if (result.Value!.StateBytes.Length > EngineLimits.MaxSerializedStateBytes)
        {
            LogSizeBudgetExceeded(nameof(CreateMatch), definition.Metadata, "state", result.Value.StateBytes.Length, EngineLimits.MaxSerializedStateBytes);
            return GameHostResult<GameStateEnvelope>.Failure(EngineErrorCode.StateTooLarge);
        }

        return result;
    }

    public EngineTransition ApplyCommand(GameStateEnvelope state, GameCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (!_registry.TryResolve(state.GameSlug, state.EngineVersion, out var definition) || definition is null)
            return EngineTransition.Reject(state.Revision, ResolveUnknownGameOrVersion(state.GameSlug));

        if (command.PayloadBytes.Length > EngineLimits.MaxCommandPayloadBytes)
        {
            _logger.LogWarning(
                "GameHost {Operation} for {Definition} rejected an oversized command payload ({Bytes} bytes).",
                nameof(ApplyCommand), definition.Metadata, command.PayloadBytes.Length);
            return EngineTransition.Reject(state.Revision, EngineErrorCode.PayloadTooLarge);
        }

        var result = Execute(ct => definition.ApplyCommand(state, command, ct), definition.Metadata, nameof(ApplyCommand), cancellationToken);
        if (!result.Succeeded)
            return EngineTransition.Reject(state.Revision, result.ErrorCode!.Value, result.ErrorDetail);

        var transition = result.Value!;
        if (!transition.Accepted)
            return transition;

        if (transition.NextState!.StateBytes.Length > EngineLimits.MaxSerializedStateBytes)
        {
            LogSizeBudgetExceeded(nameof(ApplyCommand), definition.Metadata, "next state", transition.NextState.StateBytes.Length, EngineLimits.MaxSerializedStateBytes);
            return EngineTransition.Reject(state.Revision, EngineErrorCode.StateTooLarge);
        }

        var eventBatchBytes = transition.PublicEvents.Sum(e => e.PayloadBytes.Length) + transition.PrivateEvents.Sum(e => e.PayloadBytes.Length);
        if (eventBatchBytes > EngineLimits.MaxGameEventBatchBytes)
        {
            // No dedicated Engine.* code exists for an oversized event batch — the 13 codes are a published,
            // immutable wire contract. A definition emitting more than its budget allows is a defect in the
            // definition, not a client-triggerable condition, so PluginFailure is the closest honest fit.
            LogSizeBudgetExceeded(nameof(ApplyCommand), definition.Metadata, "event batch", eventBatchBytes, EngineLimits.MaxGameEventBatchBytes);
            return EngineTransition.Reject(state.Revision, EngineErrorCode.PluginFailure, "Event batch exceeded the size budget.");
        }

        return transition;
    }

    public GameHostResult<PlayerViewEnvelope> ProjectView(GameStateEnvelope state, ViewerContext viewer, CancellationToken cancellationToken)
    {
        if (!TryResolveOrFail<PlayerViewEnvelope>(state.GameSlug, state.EngineVersion, out var definition, out var resolutionFailure))
            return resolutionFailure!;

        var result = Execute(ct => definition!.ProjectView(state, viewer, ct), definition!.Metadata, nameof(ProjectView), cancellationToken);
        if (!result.Succeeded)
            return result;

        var view = result.Value!;
        var viewBytes = view.PublicView.Length + (view.PrivateView?.Length ?? 0);
        if (viewBytes > EngineLimits.MaxPlayerViewBytes)
        {
            LogSizeBudgetExceeded(nameof(ProjectView), definition.Metadata, "player view", viewBytes, EngineLimits.MaxPlayerViewBytes);
            return GameHostResult<PlayerViewEnvelope>.Failure(EngineErrorCode.PluginFailure, "Player view exceeded the size budget.");
        }

        return result;
    }

    public GameHostResult<TerminalResultCandidate?> EvaluateResult(GameStateEnvelope state, CancellationToken cancellationToken)
    {
        if (!TryResolveOrFail<TerminalResultCandidate?>(state.GameSlug, state.EngineVersion, out var definition, out var resolutionFailure))
            return resolutionFailure!;

        return Execute(ct => definition!.EvaluateResult(state, ct), definition!.Metadata, nameof(EvaluateResult), cancellationToken);
    }

    private bool TryResolveOrFail<T>(
        string gameSlug,
        int engineVersion,
        out IHostedGameDefinition? definition,
        out GameHostResult<T>? failure)
    {
        if (_registry.TryResolve(gameSlug, engineVersion, out definition) && definition is not null)
        {
            failure = null;
            return true;
        }

        failure = GameHostResult<T>.Failure(ResolveUnknownGameOrVersion(gameSlug));
        return false;
    }

    private EngineErrorCode ResolveUnknownGameOrVersion(string gameSlug)
    {
        var slugKnown = _registry.RegisteredDefinitions.Any(m => m.Slug == gameSlug);
        return slugKnown ? EngineErrorCode.UnknownVersion : EngineErrorCode.UnknownGame;
    }

    /// <summary>
    /// Runs <paramref name="call"/> under the cooperative-cancellation watchdog: the call receives a token that
    /// is cancelled after <see cref="EngineLimits.CancellationRequestThreshold"/>, and this method gives it a
    /// further <see cref="EngineLimits.CooperativeReturnGrace"/> to observe that token and return before giving
    /// up on waiting. In-process code cannot be hard-preempted, so "giving up" means this method stops waiting
    /// and reports <see cref="EngineErrorCode.ExecutionBudgetExceeded"/> — a definition that never returns has
    /// leaked a background thread, which is a release-blocking defect, not a runtime-recoverable one.
    /// <para>
    /// Every failure path — a caller-requested cancellation, a budget timeout, a fail-closed deserialization,
    /// or an unexpected plugin exception — is normalized to a stable <see cref="EngineErrorCode"/> here. Only
    /// this method logs the underlying exception (server-side, structured, never echoed to a client); every
    /// caller sees just the code.
    /// </para>
    /// </summary>
    private GameHostResult<T> Execute<T>(
        Func<CancellationToken, T> call,
        GameDefinitionMetadata metadata,
        string operationName,
        CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
            return GameHostResult<T>.Failure(EngineErrorCode.Cancelled);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(EngineLimits.CancellationRequestThreshold);

        var task = Task.Run(() => call(cts.Token), CancellationToken.None);

        bool completedInTime;
        try
        {
            completedInTime = task.Wait(EngineLimits.CancellationRequestThreshold + EngineLimits.CooperativeReturnGrace);
        }
        catch (AggregateException)
        {
            // Wait() surfaces a faulted task by throwing; the task itself is still complete, so fall through to
            // the IsFaulted branch below to classify the failure.
            completedInTime = true;
        }

        if (!completedInTime)
        {
            _logger.LogWarning(
                "GameHost {Operation} for {Definition} did not return within its cooperative-cancellation budget.",
                operationName, metadata);
            return GameHostResult<T>.Failure(
                callerToken.IsCancellationRequested ? EngineErrorCode.Cancelled : EngineErrorCode.ExecutionBudgetExceeded);
        }

        if (task.IsFaulted)
        {
            var inner = task.Exception!.GetBaseException();

            if (inner is GameHostSerializationException serializationError)
                return GameHostResult<T>.Failure(serializationError.Code);

            if (inner is OperationCanceledException)
            {
                return GameHostResult<T>.Failure(
                    callerToken.IsCancellationRequested ? EngineErrorCode.Cancelled : EngineErrorCode.ExecutionBudgetExceeded);
            }

            _logger.LogError(inner, "GameHost plugin failure during {Operation} for {Definition}.", operationName, metadata);
            return GameHostResult<T>.Failure(EngineErrorCode.PluginFailure);
        }

        return GameHostResult<T>.Success(task.Result);
    }

    private void LogSizeBudgetExceeded(string operationName, GameDefinitionMetadata metadata, string what, int actualBytes, int maxBytes) =>
        _logger.LogWarning(
            "GameHost {Operation} for {Definition} produced a {What} of {ActualBytes} bytes, exceeding the {MaxBytes}-byte budget.",
            operationName, metadata, what, actualBytes, maxBytes);
}
