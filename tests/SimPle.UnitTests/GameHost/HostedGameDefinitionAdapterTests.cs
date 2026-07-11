using FluentAssertions;
using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.GoldenVectors;
using SimPle.UnitTests.GameHost.Reference;
using SimPle.UnitTests.GameHost.Support;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// <see cref="HostedGameDefinition{TState,TCommand,TPlayerView}"/> does all of the envelope/byte/checksum work
/// around a typed <see cref="IGameDefinition{TState,TCommand,TPlayerView}"/>. These tests exercise that adapter
/// layer directly against the <see cref="HiddenTokenDraftDefinition"/> reference engine — the identity/staleness/
/// discriminator-mismatch checks the golden vectors don't already cover.
/// </summary>
public sealed class HostedGameDefinitionAdapterTests
{
    private static readonly Guid Seat0User = HiddenTokenDraftGoldenVectorScenario.Seat0User;
    private static readonly Guid Seat1User = HiddenTokenDraftGoldenVectorScenario.Seat1User;

    private static IHostedGameDefinition CreateHosted() => HiddenTokenDraftGoldenVectorScenario.CreateHostedDefinition();

    private static GameStateEnvelope CreateTwoSeatInitialState(IHostedGameDefinition hosted)
    {
        var setup = GameSetup.Create(
            [new SeatAssignment(0, Seat0User, false), new SeatAssignment(1, Seat1User, false)],
            "multiplayer");

        return hosted.CreateInitialState(setup, HiddenTokenDraftGoldenVectorScenario.FixedMatchSeed, CancellationToken.None);
    }

    private static GameCommandEnvelope DrawEnvelope(int expectedRevision, int actorSeat, Guid actorUserId) =>
        BuildEnvelope(new DrawTokenCommand(), expectedRevision, actorSeat, actorUserId);

    private static GameCommandEnvelope BuildEnvelope(HiddenTokenDraftCommand command, int expectedRevision, int actorSeat, Guid actorUserId)
    {
        var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(command);
        return GameCommandEnvelope.Create(Guid.NewGuid(), expectedRevision, actorUserId, actorSeat, command.CommandType, payload);
    }

    [Fact]
    public void CreateInitialState_ProducesRevisionZeroWithASelfConsistentChecksum()
    {
        var hosted = CreateHosted();

        var initial = CreateTwoSeatInitialState(hosted);

        initial.Revision.Should().Be(0);
        initial.ChecksumMatches().Should().BeTrue();
        initial.GameSlug.Should().Be(hosted.Metadata.Slug);
        initial.EngineVersion.Should().Be(hosted.Metadata.EngineVersion);
        initial.StateSchemaVersion.Should().Be(hosted.Metadata.StateSchemaVersion);
    }

    [Fact]
    public void ApplyCommand_StaleExpectedRevision_RejectsWithStaleRevisionAndLeavesRevisionUnchanged()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        var transition = hosted.ApplyCommand(initial, DrawEnvelope(expectedRevision: 1, actorSeat: 0, Seat0User), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.StaleRevision.ToStableCode());
        transition.NextRevision.Should().Be(transition.PriorRevision);
        transition.NextState.Should().BeNull();
    }

    [Fact]
    public void ApplyCommand_MismatchedGameSlug_RejectsWithCorruptState()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);
        var wrongSlugState = GameStateEnvelope.Create(
            "not-this-game", initial.EngineVersion, initial.StateSchemaVersion, initial.Revision, initial.RngState, initial.StateBytes.Span);

        var transition = hosted.ApplyCommand(wrongSlugState, DrawEnvelope(0, 0, Seat0User), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.CorruptState.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_MismatchedStateSchemaVersion_RejectsWithUnsupportedStateVersion()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);
        var wrongSchemaState = GameStateEnvelope.Create(
            initial.GameSlug, initial.EngineVersion, stateSchemaVersion: 999, initial.Revision, initial.RngState, initial.StateBytes.Span);

        var transition = hosted.ApplyCommand(wrongSchemaState, DrawEnvelope(0, 0, Seat0User), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.UnsupportedStateVersion.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_TamperedChecksum_RejectsWithCorruptState()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);
        var tampered = GameStateEnvelopeTestFactory.WithTamperedChecksum(initial);

        var transition = hosted.ApplyCommand(tampered, DrawEnvelope(0, 0, Seat0User), CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.CorruptState.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_EnvelopeCommandTypeDisagreesWithPayloadDiscriminator_RejectsWithInvalidCommandType()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        // The payload's own embedded discriminator says "draw", but the envelope's out-of-band CommandType
        // claims "pass" — the adapter's defense-in-depth cross-check must catch this disagreement.
        var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(new DrawTokenCommand());
        var mismatchedEnvelope = GameCommandEnvelope.Create(Guid.NewGuid(), 0, Seat0User, 0, "pass", payload);

        var transition = hosted.ApplyCommand(initial, mismatchedEnvelope, CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.InvalidCommandType.ToStableCode());
    }

    [Fact]
    public void ApplyCommand_Accepted_AdvancesRevisionByExactlyOneAndPersistsANewRngSnapshot()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        var transition = hosted.ApplyCommand(initial, DrawEnvelope(0, 0, Seat0User), CancellationToken.None);

        transition.Accepted.Should().BeTrue();
        transition.NextState!.Revision.Should().Be(1);
        transition.NextState.ChecksumMatches().Should().BeTrue();
        transition.NextState.RngState.Should().NotBe(initial.RngState, "a draw consumes RNG output, so the persisted stream must advance");
    }

    [Fact]
    public void ProjectView_WhileInProgress_ReportsEngineStateInProgress()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        var view = hosted.ProjectView(initial, ViewerContext.ForSpectator(), CancellationToken.None);

        view.EngineState.Should().Be(EngineState.InProgress);
    }

    [Fact]
    public void ProjectView_OnceTerminal_ReportsEngineStateTerminal()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        var afterPass0 = hosted.ApplyCommand(initial, BuildEnvelope(new PassTurnCommand(), 0, 0, Seat0User), CancellationToken.None);
        var afterPass1 = hosted.ApplyCommand(afterPass0.NextState!, BuildEnvelope(new PassTurnCommand(), 1, 1, Seat1User), CancellationToken.None);

        afterPass1.EngineState.Should().Be(EngineState.Terminal);

        var view = hosted.ProjectView(afterPass1.NextState!, ViewerContext.ForSpectator(), CancellationToken.None);

        view.EngineState.Should().Be(EngineState.Terminal);
    }

    [Fact]
    public void EvaluateResult_ReturnsNullWhileInProgressAndACandidateOnceTerminal()
    {
        var hosted = CreateHosted();
        var initial = CreateTwoSeatInitialState(hosted);

        hosted.EvaluateResult(initial, CancellationToken.None).Should().BeNull();

        var afterPass0 = hosted.ApplyCommand(initial, BuildEnvelope(new PassTurnCommand(), 0, 0, Seat0User), CancellationToken.None);
        var afterPass1 = hosted.ApplyCommand(afterPass0.NextState!, BuildEnvelope(new PassTurnCommand(), 1, 1, Seat1User), CancellationToken.None);

        hosted.EvaluateResult(afterPass1.NextState!, CancellationToken.None).Should().NotBeNull();
    }
}
