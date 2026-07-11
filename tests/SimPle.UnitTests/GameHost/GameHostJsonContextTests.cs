using System.Text;
using FluentAssertions;
using SimPle.Application.GameHost.Serialization;
using SimPle.Domain.GameHost;
using SimPle.UnitTests.GameHost.Reference;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The pinned codec is the fail-closed boundary between untrusted bytes and typed game state/commands: every
/// malformed-input case here must surface as <see cref="GameHostSerializationException"/> with the caller's
/// requested <see cref="EngineErrorCode"/>, never a raw <see cref="System.Text.Json.JsonException"/>.
/// </summary>
public sealed class GameHostJsonContextTests
{
    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsAConcreteType()
    {
        var state = new HiddenTokenDraftState
        {
            SeatCount = 3,
            CurrentSeat = 1,
            ConsecutivePasses = 0,
            DeckTokens = [1, 2, 3],
            Hands = [[], [], []],
        };

        var bytes = GameHostJsonContext.Serialize(state);
        var roundTripped = GameHostJsonContext.Deserialize<HiddenTokenDraftState>(bytes, EngineErrorCode.CorruptState);

        roundTripped.SeatCount.Should().Be(3);
        roundTripped.CurrentSeat.Should().Be(1);
        roundTripped.DeckTokens.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var state = new HiddenTokenDraftState
        {
            SeatCount = 2,
            CurrentSeat = 0,
            ConsecutivePasses = 0,
            DeckTokens = [],
            Hands = [[], []],
        };

        var json = Encoding.UTF8.GetString(GameHostJsonContext.Serialize(state));

        json.Should().Contain("\"seatCount\"");
        json.Should().NotContain("\"SeatCount\"");
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsWithRequestedErrorCode()
    {
        var bytes = "{ this is not valid json"u8.ToArray();

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftState>(bytes, EngineErrorCode.CorruptState);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }

    [Fact]
    public void Deserialize_UnmappedMember_IsRejectedRatherThanIgnored()
    {
        var json = """{"seatCount":2,"currentSeat":0,"consecutivePasses":0,"deckTokens":[],"hands":[[],[]],"unexpectedField":1}""";

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftState>(Encoding.UTF8.GetBytes(json), EngineErrorCode.CorruptState);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }

    [Fact]
    public void Deserialize_NullLiteral_ThrowsWithRequestedErrorCode()
    {
        var bytes = "null"u8.ToArray();

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftState>(bytes, EngineErrorCode.CorruptState);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }

    [Fact]
    public void Deserialize_NumberAsString_IsRejectedUnderStrictNumberHandling()
    {
        // NumberHandling.Strict disallows a quoted number for an int property.
        var json = """{"seatCount":"2","currentSeat":0,"consecutivePasses":0,"deckTokens":[],"hands":[[],[]]}""";

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftState>(Encoding.UTF8.GetBytes(json), EngineErrorCode.CorruptState);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }

    [Fact]
    public void Deserialize_UnknownPolymorphicDiscriminator_MapsToRequestedErrorCode()
    {
        var json = """{"type":"not-a-real-command"}""";

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftCommand>(Encoding.UTF8.GetBytes(json), EngineErrorCode.InvalidCommandType);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.InvalidCommandType);
    }

    [Fact]
    public void Serialize_PolymorphicCommand_WritesTheDeclaredTypeDiscriminator()
    {
        var bytes = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(new DrawTokenCommand());
        var json = Encoding.UTF8.GetString(bytes);

        json.Should().Contain("\"type\":\"draw\"");
    }

    [Fact]
    public void Deserialize_PolymorphicCommand_ResolvesToTheConcreteDerivedType()
    {
        var bytes = GameHostJsonContext.Serialize<HiddenTokenDraftCommand>(new PassTurnCommand());

        var result = GameHostJsonContext.Deserialize<HiddenTokenDraftCommand>(bytes, EngineErrorCode.InvalidCommandType);

        result.Should().BeOfType<PassTurnCommand>();
        result.CommandType.Should().Be("pass");
    }

    [Fact]
    public void Deserialize_TrailingGarbageAfterValidJson_IsRejected()
    {
        var json = """{"seatCount":2,"currentSeat":0,"consecutivePasses":0,"deckTokens":[],"hands":[[],[]]} garbage""";

        var act = () => GameHostJsonContext.Deserialize<HiddenTokenDraftState>(Encoding.UTF8.GetBytes(json), EngineErrorCode.CorruptState);

        act.Should().Throw<GameHostSerializationException>().Which.Code.Should().Be(EngineErrorCode.CorruptState);
    }
}
