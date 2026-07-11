using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Reference;

namespace SimPle.IntegrationTests.GameHost;

/// <summary>
/// The "M8 boundary": Module 8 (not yet built) will resolve <see cref="IGameHostInvoker"/> and
/// <see cref="IGameRegistry"/> from the real <c>SimPle.Api</c> DI container and drive a match through exactly the
/// call sequence exercised here. These tests boot the actual composition root (all of Program.cs's service
/// registrations, options validation, and startup checks — not a hand-rolled service collection) and prove the
/// resolved services round-trip a full match lifecycle for the <see cref="HiddenTokenDraftDefinition"/> reference
/// engine. Unit tests already cover every branch of the invoker/adapter in isolation; this suite's job is only to
/// prove the wiring — wrong-lifetime registrations, missing services, DI resolution order — is correct end to end.
/// </summary>
public sealed class GameHostCompositionRootTests : IDisposable
{
    private static readonly Guid Seat0User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid Seat1User = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private readonly GameHostTestWebApplicationFactory _factory = new(
        new HostedGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView>(
            new HiddenTokenDraftDefinition()));

    [Fact]
    public void DefaultCompositionRoot_WithZeroInstalledEngines_ResolvesAnEmptyRegistry()
    {
        using var defaultFactory = new GameHostTestWebApplicationFactory();

        var registry = defaultFactory.Services.GetRequiredService<IGameRegistry>();

        registry.RegisteredDefinitions.Should().BeEmpty("production installs no Phase-2 engine yet");
    }

    [Fact]
    public void CompositionRoot_ResolvesGameHostInvokerAndRegistryAsExpectedLifetimesWithoutThrowing()
    {
        using var scope = _factory.Services.CreateScope();

        var invoker = scope.ServiceProvider.GetRequiredService<IGameHostInvoker>();
        var registry = scope.ServiceProvider.GetRequiredService<IGameRegistry>();

        invoker.Should().NotBeNull();
        registry.RegisteredDefinitions.Should().ContainSingle(m => m.Slug == HiddenTokenDraftDefinition.Slug);
    }

    [Fact]
    public void FullMatchLifecycle_ThroughTheDIResolvedInvoker_PlaysToATerminalResult()
    {
        using var scope = _factory.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IGameHostInvoker>();

        var setup = GameSetup.Create(
            [new SeatAssignment(0, Seat0User, false), new SeatAssignment(1, Seat1User, false)],
            "multiplayer");

        var createResult = invoker.CreateMatch(
            HiddenTokenDraftDefinition.Slug, engineVersion: 1, setup, matchSeed: 42UL, CancellationToken.None);

        createResult.Succeeded.Should().BeTrue();
        var state = createResult.Value!;
        state.Revision.Should().Be(0);

        var pass0 = invoker.ApplyCommand(state, PassEnvelope(expectedRevision: 0, actorSeat: 0, Seat0User), CancellationToken.None);
        pass0.Accepted.Should().BeTrue();

        var pass1 = invoker.ApplyCommand(pass0.NextState!, PassEnvelope(expectedRevision: 1, actorSeat: 1, Seat1User), CancellationToken.None);
        pass1.Accepted.Should().BeTrue();
        pass1.EngineState.Should().Be(EngineState.Terminal, "a full round of consecutive passes ends the match");

        var view = invoker.ProjectView(pass1.NextState!, ViewerContext.ForSpectator(), CancellationToken.None);
        view.Succeeded.Should().BeTrue();
        view.Value!.EngineState.Should().Be(EngineState.Terminal);

        var result = invoker.EvaluateResult(pass1.NextState!, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.SeatResults.Should().HaveCount(2);
    }

    [Fact]
    public void CreateMatch_UnknownEngineVersion_ReturnsUnknownVersionThroughTheRealInvoker()
    {
        using var scope = _factory.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IGameHostInvoker>();

        var setup = GameSetup.Create(
            [new SeatAssignment(0, Seat0User, false), new SeatAssignment(1, Seat1User, false)],
            "multiplayer");

        var result = invoker.CreateMatch(HiddenTokenDraftDefinition.Slug, engineVersion: 999, setup, matchSeed: 1UL, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(EngineErrorCode.UnknownVersion);
    }

    private static GameCommandEnvelope PassEnvelope(int expectedRevision, int actorSeat, Guid actorUserId)
    {
        var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(new PassTurnCommand());
        return GameCommandEnvelope.Create(Guid.NewGuid(), expectedRevision, actorUserId, actorSeat, "pass", payload);
    }

    public void Dispose() => _factory.Dispose();
}
