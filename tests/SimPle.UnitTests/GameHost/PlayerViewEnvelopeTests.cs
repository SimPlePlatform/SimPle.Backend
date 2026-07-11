using System.Text;
using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The player view is the redaction boundary — the only game state a client ever receives. The rule this class
/// exists to enforce is that a spectator cannot be handed private data <i>even by a definition that tries to</i>:
/// the envelope refuses to construct, rather than trusting each future game author to remember.
/// </summary>
public sealed class PlayerViewEnvelopeTests
{
    private static readonly byte[] PublicBytes = Encoding.UTF8.GetBytes("""{"turn":1,"counts":[3,3]}""");
    private static readonly byte[] PrivateBytes = Encoding.UTF8.GetBytes("""{"hand":["A","K"]}""");

    [Fact]
    public void Create_ForAPlayer_CarriesBothProjections()
    {
        var envelope = PlayerViewEnvelope.Create(
            revision: 4,
            viewer: ViewerContext.ForPlayer(seat: 1, userId: Guid.NewGuid()),
            publicView: PublicBytes,
            viewSchemaVersion: 1,
            engineState: EngineState.InProgress,
            privateView: PrivateBytes,
            hasPrivateView: true);

        envelope.ViewerRole.Should().Be(ViewerRole.Player);
        envelope.ViewerSeat.Should().Be(1);
        envelope.PublicView.ToArray().Should().Equal(PublicBytes);
        envelope.PrivateView!.Value.ToArray().Should().Equal(PrivateBytes);
    }

    [Fact]
    public void Create_ForASpectatorCarryingPrivateData_Throws()
    {
        // The whole point of the type. A definition that passes a hand into a spectator projection has made a
        // hidden-information mistake, and it fails here rather than on a client screen.
        var create = () => PlayerViewEnvelope.Create(
            revision: 4,
            viewer: ViewerContext.ForSpectator(),
            publicView: PublicBytes,
            viewSchemaVersion: 1,
            engineState: EngineState.InProgress,
            privateView: PrivateBytes,
            hasPrivateView: true);

        create.Should().Throw<ArgumentException>().WithMessage("*must not carry private data*");
    }

    [Fact]
    public void Create_ForASpectator_YieldsOnlyThePublicProjection()
    {
        var envelope = PlayerViewEnvelope.Create(
            revision: 4,
            viewer: ViewerContext.ForSpectator(),
            publicView: PublicBytes,
            viewSchemaVersion: 1,
            engineState: EngineState.InProgress);

        envelope.ViewerRole.Should().Be(ViewerRole.Spectator);
        envelope.ViewerSeat.Should().BeNull();
        envelope.PrivateView.Should().BeNull();
        envelope.PublicView.ToArray().Should().Equal(PublicBytes);
    }

    [Fact]
    public void Create_ForAPlayerWithoutASeat_Throws()
    {
        var seatless = new ViewerContext(ViewerRole.Player, Seat: null, UserId: Guid.NewGuid());

        var create = () => PlayerViewEnvelope.Create(
            revision: 0, seatless, PublicBytes, 1, EngineState.InProgress);

        create.Should().Throw<ArgumentException>().WithMessage("*requires a seat*");
    }

    [Fact]
    public void Create_WithNonPositiveViewSchemaVersion_Throws()
    {
        var create = () => PlayerViewEnvelope.Create(
            revision: 0,
            viewer: ViewerContext.ForSpectator(),
            publicView: PublicBytes,
            viewSchemaVersion: 0,
            engineState: EngineState.InProgress);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithNegativeRevision_Throws()
    {
        var create = () => PlayerViewEnvelope.Create(
            revision: -1,
            viewer: ViewerContext.ForSpectator(),
            publicView: PublicBytes,
            viewSchemaVersion: 1,
            engineState: EngineState.InProgress);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_CopiesTheCallersBuffers()
    {
        var mutable = Encoding.UTF8.GetBytes("""{"turn":1}""");

        var envelope = PlayerViewEnvelope.Create(
            revision: 1,
            viewer: ViewerContext.ForSpectator(),
            publicView: mutable,
            viewSchemaVersion: 1,
            engineState: EngineState.InProgress);

        mutable[2] = (byte)'X';

        envelope.PublicView.ToArray().Should().NotEqual(mutable, "the envelope must own the bytes it hands out");
    }
}
