using System.Text;
using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The command envelope is where client input meets server authority. The actor and seat are bound by Module 8
/// from authenticated membership and live <i>outside</i> the payload, so a payload claiming a different actor
/// is structurally unable to influence anything — that is the anti-spoofing design, and these tests pin it.
/// </summary>
public sealed class GameCommandEnvelopeTests
{
    private static GameCommandEnvelope Create(
        Guid? commandId = null,
        int expectedRevision = 0,
        Guid? actorUserId = null,
        int actorSeat = 0,
        string commandType = "Draw",
        byte[]? payload = null) =>
        GameCommandEnvelope.Create(
            commandId ?? Guid.NewGuid(),
            expectedRevision,
            actorUserId ?? Guid.NewGuid(),
            actorSeat,
            commandType,
            payload ?? Encoding.UTF8.GetBytes("{}"));

    [Fact]
    public void Create_CarriesTheServerBoundActorOutsideThePayload()
    {
        var actor = Guid.NewGuid();

        var envelope = Create(actorUserId: actor, actorSeat: 3);

        envelope.ActorUserId.Should().Be(actor);
        envelope.ActorSeat.Should().Be(3);
    }

    [Fact]
    public void Create_WithAnOversizedPayload_DoesNotThrow()
    {
        // Deliberate: size is a host-boundary concern that must surface as a typed Engine.PayloadTooLarge
        // rejection, not as an exception the host then has to catch and translate. The envelope stays a dumb
        // carrier so the invoker (slice 5B) owns that decision in one place.
        var oversized = new byte[EngineLimits.MaxCommandPayloadBytes + 1];

        var envelope = Create(payload: oversized);

        envelope.PayloadBytes.Length.Should().Be(EngineLimits.MaxCommandPayloadBytes + 1);
    }

    [Fact]
    public void Create_CopiesTheCallersPayloadBuffer()
    {
        var buffer = Encoding.UTF8.GetBytes("""{"token":"A"}""");
        var envelope = Create(payload: buffer);

        buffer[2] = (byte)'X';

        envelope.PayloadBytes.ToArray().Should().NotEqual(buffer);
    }

    [Fact]
    public void Create_WithAnEmptyCommandId_Throws()
    {
        var create = () => Create(commandId: Guid.Empty);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithAnEmptyActorUserId_Throws()
    {
        var create = () => Create(actorUserId: Guid.Empty);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithANegativeSeat_Throws()
    {
        var create = () => Create(actorSeat: -1);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithANegativeExpectedRevision_Throws()
    {
        var create = () => Create(expectedRevision: -1);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithAnEmptyCommandType_Throws()
    {
        // The command type is a stable allow-listed discriminator, never a CLR type name. An empty one would
        // mean the codec had nothing to resolve against.
        var create = () => Create(commandType: "  ");

        create.Should().Throw<ArgumentException>();
    }
}
