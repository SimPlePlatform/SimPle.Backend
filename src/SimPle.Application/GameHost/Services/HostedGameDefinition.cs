using SimPle.Application.GameHost.Serialization;
using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Services;

/// <summary>
/// The one <see cref="IHostedGameDefinition"/> implementation. Wraps a single typed
/// <see cref="IGameDefinition{TState,TCommand,TPlayerView}"/> and does all of the envelope/byte/checksum work so
/// the typed definition stays pure game rules.
/// </summary>
public sealed class HostedGameDefinition<TState, TCommand, TPlayerView> : IHostedGameDefinition
    where TState : class
    where TCommand : class, IGameCommand
    where TPlayerView : class
{
    private readonly IGameDefinition<TState, TCommand, TPlayerView> _definition;

    public HostedGameDefinition(IGameDefinition<TState, TCommand, TPlayerView> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public GameDefinitionMetadata Metadata => _definition.Metadata;

    public GameStateEnvelope CreateInitialState(GameSetup setup, UInt128 matchSeed, CancellationToken cancellationToken)
    {
        var rng = Pcg32.FromMatchSeed(matchSeed);
        var state = _definition.CreateInitialState(setup, rng, cancellationToken);
        var stateBytes = GameHostJsonContext.Serialize(state);

        return GameStateEnvelope.Create(
            Metadata.Slug,
            Metadata.EngineVersion,
            Metadata.StateSchemaVersion,
            revision: 0,
            rng.Snapshot(),
            stateBytes);
    }

    public EngineTransition ApplyCommand(GameStateEnvelope state, GameCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (!TryValidateStateIdentity(state, out var identityFailure))
            return EngineTransition.Reject(state.Revision, identityFailure);

        if (command.ExpectedRevision != state.Revision)
            return EngineTransition.Reject(state.Revision, EngineErrorCode.StaleRevision);

        TState typedState;
        TCommand typedCommand;
        try
        {
            typedState = GameHostJsonContext.Deserialize<TState>(state.StateBytes.Span, EngineErrorCode.CorruptState);
            typedCommand = GameHostJsonContext.Deserialize<TCommand>(command.PayloadBytes.Span, EngineErrorCode.InvalidCommandType);
        }
        catch (GameHostSerializationException ex)
        {
            return EngineTransition.Reject(state.Revision, ex.Code);
        }

        // Defense-in-depth: the payload can only deserialize into a derived type this game's own TCommand union
        // declares, so a cross-definition discriminator already fails above. This catches the narrower case
        // where the envelope's out-of-band CommandType and the payload's own embedded discriminator disagree.
        if (!string.Equals(typedCommand.CommandType, command.CommandType, StringComparison.Ordinal))
        {
            return EngineTransition.Reject(
                state.Revision,
                EngineErrorCode.InvalidCommandType,
                "Envelope CommandType does not match the payload's declared type.");
        }

        var context = new CommandContext(command.CommandId, command.ActorUserId, command.ActorSeat, command.ExpectedRevision, state.Revision);
        var rng = Pcg32.Restore(state.RngState);

        // A rejected decision leaves `rng` un-persisted: any draws it made before rejecting are discarded along
        // with everything else, exactly as IGameDefinition.ApplyCommand's contract requires. An exception from
        // the typed call is deliberately not caught here — it propagates to GameHostInvoker, which is the layer
        // responsible for mapping an unexpected plugin failure to Engine.PluginFailure without ever logging or
        // returning the raw exception text.
        var decision = _definition.ApplyCommand(typedState, typedCommand, context, rng, cancellationToken);

        if (!decision.Accepted)
            return EngineTransition.Reject(state.Revision, decision.RejectionCode!.Value, decision.RejectionDetail);

        var nextStateBytes = GameHostJsonContext.Serialize(decision.NextState);
        var nextEnvelope = GameStateEnvelope.Create(
            state.GameSlug,
            state.EngineVersion,
            state.StateSchemaVersion,
            state.Revision + 1,
            rng.Snapshot(),
            nextStateBytes);

        return EngineTransition.Accept(state.Revision, nextEnvelope, decision.Events, decision.TerminalResult);
    }

    public PlayerViewEnvelope ProjectView(GameStateEnvelope state, ViewerContext viewer, CancellationToken cancellationToken)
    {
        var typedState = DeserializeStateOrThrow(state);
        var view = _definition.ProjectView(typedState, viewer, cancellationToken);
        var viewBytes = GameHostJsonContext.Serialize(view);
        var terminal = _definition.EvaluateResult(typedState, cancellationToken);

        // TPlayerView is already the complete, redacted-for-this-viewer projection — the typed contract returns
        // one object per viewer, not a (shared, addressee-only) pair. The whole projection is carried as
        // PublicView; PrivateView/hasPrivateView stays unused by this adapter. This does not weaken redaction:
        // a spectator's TPlayerView and a player's TPlayerView are already distinct, correctly-scoped objects
        // built by the definition's own ProjectView call for that specific viewer.
        return PlayerViewEnvelope.Create(
            state.Revision,
            viewer,
            viewBytes,
            Metadata.StateSchemaVersion,
            terminal is null ? EngineState.InProgress : EngineState.Terminal);
    }

    public TerminalResultCandidate? EvaluateResult(GameStateEnvelope state, CancellationToken cancellationToken)
    {
        var typedState = DeserializeStateOrThrow(state);
        return _definition.EvaluateResult(typedState, cancellationToken);
    }

    private TState DeserializeStateOrThrow(GameStateEnvelope state)
    {
        if (!TryValidateStateIdentity(state, out var identityFailure))
            throw new GameHostSerializationException(identityFailure, "State envelope failed identity or integrity validation.");

        return GameHostJsonContext.Deserialize<TState>(state.StateBytes.Span, EngineErrorCode.CorruptState);
    }

    private bool TryValidateStateIdentity(GameStateEnvelope state, out EngineErrorCode failureCode)
    {
        if (state.GameSlug != Metadata.Slug || state.EngineVersion != Metadata.EngineVersion)
        {
            failureCode = EngineErrorCode.CorruptState;
            return false;
        }

        if (state.StateSchemaVersion != Metadata.StateSchemaVersion)
        {
            failureCode = EngineErrorCode.UnsupportedStateVersion;
            return false;
        }

        if (!state.ChecksumMatches())
        {
            failureCode = EngineErrorCode.CorruptState;
            return false;
        }

        failureCode = default;
        return true;
    }
}
