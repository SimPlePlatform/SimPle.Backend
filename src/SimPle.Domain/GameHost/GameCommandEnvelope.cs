namespace SimPle.Domain.GameHost;

/// <summary>
/// One attempted move, as the host receives it.
/// <para>
/// <see cref="ActorUserId"/> and <see cref="ActorSeat"/> are <b>server-bound</b>: Module 8 derives them from
/// authenticated lobby membership before constructing this envelope. Nothing inside <see cref="PayloadBytes"/>
/// may override them — a payload that claims a different actor is simply ignored, which is what makes seat
/// spoofing structurally impossible rather than merely checked for.
/// </para>
/// <para>
/// <see cref="PayloadBytes"/> is untrusted input. Size is deliberately <i>not</i> validated here: the host
/// boundary checks it and returns a typed <see cref="EngineErrorCode.PayloadTooLarge"/> rather than throwing,
/// so an oversized command is a clean rejection and not an exception to be mapped.
/// </para>
/// </summary>
public sealed class GameCommandEnvelope
{
    /// <summary>Idempotency key. Module 8 stores the receipt; Module 5 only carries it.</summary>
    public Guid CommandId { get; }

    /// <summary>The revision the caller believes it is acting on. A mismatch is <see cref="EngineErrorCode.StaleRevision"/>.</summary>
    public int ExpectedRevision { get; }

    public Guid ActorUserId { get; }
    public int ActorSeat { get; }

    /// <summary>Stable command discriminator, resolved against the definition's allow-list — never a CLR type name.</summary>
    public string CommandType { get; }

    public ReadOnlyMemory<byte> PayloadBytes { get; }

    private GameCommandEnvelope(
        Guid commandId,
        int expectedRevision,
        Guid actorUserId,
        int actorSeat,
        string commandType,
        ReadOnlyMemory<byte> payloadBytes)
    {
        CommandId = commandId;
        ExpectedRevision = expectedRevision;
        ActorUserId = actorUserId;
        ActorSeat = actorSeat;
        CommandType = commandType;
        PayloadBytes = payloadBytes;
    }

    public static GameCommandEnvelope Create(
        Guid commandId,
        int expectedRevision,
        Guid actorUserId,
        int actorSeat,
        string commandType,
        ReadOnlySpan<byte> payloadBytes)
    {
        if (commandId == Guid.Empty)
            throw new ArgumentException("CommandId must not be empty.", nameof(commandId));
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), expectedRevision, "ExpectedRevision must be non-negative.");
        if (actorUserId == Guid.Empty)
            throw new ArgumentException("ActorUserId must not be empty.", nameof(actorUserId));
        if (actorSeat < 0)
            throw new ArgumentOutOfRangeException(nameof(actorSeat), actorSeat, "ActorSeat must be non-negative.");
        if (string.IsNullOrWhiteSpace(commandType))
            throw new ArgumentException("CommandType must not be empty.", nameof(commandType));

        return new GameCommandEnvelope(
            commandId,
            expectedRevision,
            actorUserId,
            actorSeat,
            commandType,
            payloadBytes.ToArray());
    }
}
