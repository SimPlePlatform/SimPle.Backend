using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Support;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// <see cref="GameHostInvoker"/> is the host boundary every call crosses: resolve, check inbound size, run
/// under the cooperative-cancellation watchdog, check outbound size, normalize every failure to a stable
/// <see cref="EngineErrorCode"/>. Each of those steps gets its own fault-injected case here via
/// <see cref="FakeHostedGameDefinition"/>, since the real reference engine cannot be made to hang, throw, or
/// overflow a size budget on demand.
/// </summary>
public sealed class GameHostInvokerTests
{
    private const string Slug = "hidden-token-draft";
    private static readonly Guid AnyUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AnotherUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Pcg32State AnyRngState = Pcg32.FromSeedParts(1UL, 2UL).Snapshot();

    private static GameDefinitionMetadata Metadata(string slug = Slug, int engineVersion = 1) => GameDefinitionMetadata.Create(
        slug: slug, engineVersion: engineVersion, stateSchemaVersion: 1, minPlayers: 2, maxPlayers: 4, supportedModes: ["multiplayer"]);

    private static GameSetup TwoSeatSetup() => GameSetup.Create(
        [new SeatAssignment(0, AnyUser, false), new SeatAssignment(1, AnotherUser, false)], "multiplayer");

    private static GameStateEnvelope StateEnvelope(string slug = Slug, int engineVersion = 1, int stateBytesLength = 16, int revision = 0) =>
        GameStateEnvelope.Create(slug, engineVersion, stateSchemaVersion: 1, revision, AnyRngState, new byte[stateBytesLength]);

    private static GameCommandEnvelope CommandEnvelope(int payloadLength = 16, int expectedRevision = 0) =>
        GameCommandEnvelope.Create(Guid.NewGuid(), expectedRevision, AnyUser, actorSeat: 0, "draw", new byte[payloadLength]);

    private static GameHostInvoker InvokerWith(params FakeHostedGameDefinition[] definitions) =>
        new(GameRegistry.Create(definitions), NullLogger<GameHostInvoker>.Instance);

    // ── Resolution ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateMatch_UnknownSlug_ReturnsUnknownGame()
    {
        var invoker = InvokerWith(new FakeHostedGameDefinition(Metadata()));

        var result = invoker.CreateMatch("no-such-game", 1, TwoSeatSetup(), 0, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.UnknownGame);
    }

    [Fact]
    public void CreateMatch_KnownSlugButUnregisteredEngineVersion_ReturnsUnknownVersionNotUnknownGame()
    {
        var invoker = InvokerWith(new FakeHostedGameDefinition(Metadata(engineVersion: 1)));

        var result = invoker.CreateMatch(Slug, 99, TwoSeatSetup(), 0, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.UnknownVersion);
    }

    [Fact]
    public void CreateMatch_Success_ReturnsTheProducedEnvelope()
    {
        var envelope = StateEnvelope();
        var fake = new FakeHostedGameDefinition(Metadata()) { OnCreateInitialState = (_, _, _) => envelope };
        var invoker = InvokerWith(fake);

        var result = invoker.CreateMatch(Slug, 1, TwoSeatSetup(), 0, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().BeSameAs(envelope);
    }

    [Fact]
    public void CreateMatch_OversizedState_ReturnsStateTooLarge()
    {
        var tooBig = StateEnvelope(stateBytesLength: EngineLimits.MaxSerializedStateBytes + 1);
        var fake = new FakeHostedGameDefinition(Metadata()) { OnCreateInitialState = (_, _, _) => tooBig };
        var invoker = InvokerWith(fake);

        var result = invoker.CreateMatch(Slug, 1, TwoSeatSetup(), 0, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.StateTooLarge);
    }

    // ── ApplyCommand ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyCommand_UnregisteredGameSlugOnTheStateEnvelope_RejectsUnknownGame()
    {
        var invoker = InvokerWith(new FakeHostedGameDefinition(Metadata()));
        var state = StateEnvelope(slug: "no-such-game");

        var transition = invoker.ApplyCommand(state, CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.UnknownGame.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_OversizedPayload_RejectsPayloadTooLargeWithoutCallingTheDefinition()
    {
        var called = false;
        var fake = new FakeHostedGameDefinition(Metadata()) { OnApplyCommand = (_, _, _) => { called = true; throw new InvalidOperationException(); } };
        var invoker = InvokerWith(fake);
        var command = CommandEnvelope(payloadLength: EngineLimits.MaxCommandPayloadBytes + 1);

        var transition = invoker.ApplyCommand(StateEnvelope(), command, CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.PayloadTooLarge.ToStableCode());
        called.Should().BeFalse("an oversized payload must be rejected before the definition is ever invoked");
    }

    [Fact]
    public void ApplyCommand_DefinitionThrowsGameHostSerializationException_MapsToItsCarriedCode()
    {
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            OnApplyCommand = (_, _, _) => throw new GameHostSerializationException(EngineErrorCode.CorruptState, "bad state"),
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.CorruptState.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_DefinitionThrowsAnUnexpectedException_MapsToPluginFailureWithoutLeakingExceptionText()
    {
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            OnApplyCommand = (_, _, _) => throw new InvalidOperationException("some internal engine bug, never seen by a client"),
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.PluginFailure.ToStableCode());
        transition.RejectionDetail.Should().NotContain("some internal engine bug");
    }

    [Fact]
    public void ApplyCommand_CallerAlreadyCancelled_ReturnsCancelledWithoutInvokingTheDefinition()
    {
        var called = false;
        var fake = new FakeHostedGameDefinition(Metadata()) { OnApplyCommand = (_, _, _) => { called = true; throw new InvalidOperationException(); } };
        var invoker = InvokerWith(fake);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), cts.Token);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.Cancelled.ToStableCode());
        called.Should().BeFalse();
    }

    [Fact]
    public void ApplyCommand_DefinitionExceedsTheCooperativeCancellationBudget_ReturnsExecutionBudgetExceeded()
    {
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            // Ignores the token entirely, standing in for a definition that has leaked/blocked a thread; the
            // invoker must stop waiting once CancellationRequestThreshold + CooperativeReturnGrace elapses
            // rather than blocking the caller indefinitely.
            OnApplyCommand = (state, _, _) =>
            {
                Thread.Sleep(2000);
                return EngineTransition.Reject(state.Revision, EngineErrorCode.InvalidCommand);
            },
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.ExecutionBudgetExceeded.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_AcceptedButOversizedNextState_RejectsStateTooLarge()
    {
        var tooBigNext = StateEnvelope(stateBytesLength: EngineLimits.MaxSerializedStateBytes + 1, revision: 1);
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            OnApplyCommand = (state, _, _) => EngineTransition.Accept(state.Revision, tooBigNext, [], null),
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.StateTooLarge.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_AcceptedButOversizedEventBatch_RejectsPluginFailure()
    {
        var nextState = StateEnvelope(revision: 1);
        var oversizedEvent = GameEvent.Public("some-event", schemaVersion: 1, new byte[EngineLimits.MaxGameEventBatchBytes + 1]);
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            OnApplyCommand = (state, _, _) => EngineTransition.Accept(state.Revision, nextState, [oversizedEvent], null),
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.PluginFailure.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_AcceptedWithinBudget_PassesTheTransitionThrough()
    {
        var nextState = StateEnvelope(revision: 1);
        var fake = new FakeHostedGameDefinition(Metadata())
        {
            OnApplyCommand = (state, _, _) => EngineTransition.Accept(state.Revision, nextState, [], null),
        };
        var invoker = InvokerWith(fake);

        var transition = invoker.ApplyCommand(StateEnvelope(), CommandEnvelope(), CancellationToken.None);

        transition.Accepted.Should().BeTrue();
        transition.NextState.Should().BeSameAs(nextState);
    }

    // ── ProjectView / EvaluateResult ────────────────────────────────────────

    [Fact]
    public void ProjectView_UnknownGame_ReturnsUnknownGame()
    {
        var invoker = InvokerWith(new FakeHostedGameDefinition(Metadata()));

        var result = invoker.ProjectView(StateEnvelope(slug: "no-such-game"), ViewerContext.ForSpectator(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.UnknownGame);
    }

    [Fact]
    public void ProjectView_OversizedView_ReturnsPluginFailure()
    {
        var oversizedView = PlayerViewEnvelope.Create(
            0, ViewerContext.ForSpectator(), new byte[EngineLimits.MaxPlayerViewBytes + 1], viewSchemaVersion: 1, EngineState.InProgress);
        var fake = new FakeHostedGameDefinition(Metadata()) { OnProjectView = (_, _, _) => oversizedView };
        var invoker = InvokerWith(fake);

        var result = invoker.ProjectView(StateEnvelope(), ViewerContext.ForSpectator(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.PluginFailure);
    }

    [Fact]
    public void ProjectView_WithinBudget_ReturnsTheProducedView()
    {
        var view = PlayerViewEnvelope.Create(0, ViewerContext.ForSpectator(), new byte[8], viewSchemaVersion: 1, EngineState.InProgress);
        var fake = new FakeHostedGameDefinition(Metadata()) { OnProjectView = (_, _, _) => view };
        var invoker = InvokerWith(fake);

        var result = invoker.ProjectView(StateEnvelope(), ViewerContext.ForSpectator(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().BeSameAs(view);
    }

    [Fact]
    public void EvaluateResult_UnknownGame_ReturnsUnknownGame()
    {
        var invoker = InvokerWith(new FakeHostedGameDefinition(Metadata()));

        var result = invoker.EvaluateResult(StateEnvelope(slug: "no-such-game"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.UnknownGame);
    }

    [Fact]
    public void EvaluateResult_DelegatesToTheDefinitionAndReturnsItsCandidate()
    {
        var fake = new FakeHostedGameDefinition(Metadata()) { OnEvaluateResult = (_, _) => null };
        var invoker = InvokerWith(fake);

        var result = invoker.EvaluateResult(StateEnvelope(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}
