namespace SimPle.Domain.GameHost;

/// <summary>
/// The contract every member of a game's <c>TCommand</c> union must satisfy. <see cref="CommandType"/> must equal
/// the literal a derived command is declared under via <c>[JsonDerivedType(typeof(X), "literal")]</c> on the
/// union's base type.
/// <para>
/// The host adapter deserializes <see cref="GameCommandEnvelope.PayloadBytes"/> into the concrete
/// <c>TCommand</c> union — which only recognizes derived types declared by that game's own hierarchy, so a
/// payload built for a different game's command type fails deserialization outright — and then compares
/// <see cref="CommandType"/> against <see cref="GameCommandEnvelope.CommandType"/>. A mismatch between the two
/// is rejected before the typed definition ever sees the command: it means the envelope's out-of-band
/// discriminator and the payload's embedded discriminator disagree, which is only possible under tampering or a
/// client bug, never under normal operation.
/// </para>
/// </summary>
public interface IGameCommand
{
    string CommandType { get; }
}
