using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Application.GameHost.Serialization;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Reference;

namespace SimPle.IntegrationTests.GameHost;

/// <summary>
/// <see cref="GameHostJsonContext"/>'s own XML doc explicitly flags one adversarial shape as owed to "the
/// serializer-hardening test suite": a polymorphic payload whose type-discriminator property is not first in the
/// JSON object. .NET 8's polymorphic deserializer requires the discriminator first and throws otherwise — the
/// behavior <c>AllowOutOfOrderMetadataProperties</c> (added in .NET 9, not available/used here) would relax. This
/// suite proves that failure is caught fail-closed at the real DI-resolved <see cref="IGameHostInvoker"/>
/// boundary — the actual ingestion path untrusted command bytes travel — not just at the codec in isolation
/// (already covered by <c>GameHostJsonContextTests</c> in the unit suite).
/// </summary>
public sealed class GameHostSerializerHardeningTests : IDisposable
{
    private static readonly Guid Seat0User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid Seat1User = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private readonly GameHostTestWebApplicationFactory _factory = new(
        new HostedGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView>(
            new HiddenTokenDraftDefinition()));

    [Fact]
    public void ApplyCommand_OutOfOrderTypeDiscriminator_IsRejectedNotThrownThroughTheInvoker()
    {
        using var scope = _factory.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IGameHostInvoker>();
        var state = CreateTwoSeatMatch(invoker);

        // A well-formed "draw" command, except the "type" discriminator is the second property rather than the
        // first — the exact adversarial shape .NET 8's strict (in-order-only) polymorphic reader must reject.
        var outOfOrderPayload = Encoding.UTF8.GetBytes("""{"decoy":1,"type":"draw"}""");
        var envelope = GameCommandEnvelope.Create(Guid.NewGuid(), expectedRevision: 0, Seat0User, actorSeat: 0, "draw", outOfOrderPayload);

        var transition = invoker.ApplyCommand(state, envelope, CancellationToken.None);

        transition.Accepted.Should().BeFalse();
        transition.RejectionCode.Should().Be(EngineErrorCode.InvalidCommandType.ToStableCode());
        transition.NextRevision.Should().Be(transition.PriorRevision);
    }

    [Fact]
    public void ApplyCommand_WellFormedInOrderDiscriminator_IsAcceptedForComparison()
    {
        using var scope = _factory.Services.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IGameHostInvoker>();
        var state = CreateTwoSeatMatch(invoker);

        var payload = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(new DrawTokenCommand());
        var envelope = GameCommandEnvelope.Create(Guid.NewGuid(), expectedRevision: 0, Seat0User, actorSeat: 0, "draw", payload);

        var transition = invoker.ApplyCommand(state, envelope, CancellationToken.None);

        transition.Accepted.Should().BeTrue("a correctly-ordered discriminator must not be caught by the hardening check");
    }

    private GameStateEnvelope CreateTwoSeatMatch(IGameHostInvoker invoker)
    {
        var setup = GameSetup.Create(
            [new SeatAssignment(0, Seat0User, false), new SeatAssignment(1, Seat1User, false)],
            "multiplayer");

        var result = invoker.CreateMatch(HiddenTokenDraftDefinition.Slug, engineVersion: 1, setup, matchSeed: 7UL, CancellationToken.None);
        result.Succeeded.Should().BeTrue();
        return result.Value!;
    }

    public void Dispose() => _factory.Dispose();
}
