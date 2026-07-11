using System.Text.Json.Serialization;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.Reference;

/// <summary>
/// The command union for <see cref="HiddenTokenDraftDefinition"/>. Members are allow-listed by the
/// <see cref="JsonDerivedTypeAttribute"/> string discriminator, never by CLR type name, matching every other
/// game-host command union.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(DrawTokenCommand), "draw")]
[JsonDerivedType(typeof(PassTurnCommand), "pass")]
public abstract class HiddenTokenDraftCommand : IGameCommand
{
    public abstract string CommandType { get; }
}

/// <summary>Draw one random remaining token from the shared pool into the acting seat's hand.</summary>
public sealed class DrawTokenCommand : HiddenTokenDraftCommand
{
    public override string CommandType => "draw";
}

/// <summary>End the acting seat's turn without drawing.</summary>
public sealed class PassTurnCommand : HiddenTokenDraftCommand
{
    public override string CommandType => "pass";
}
