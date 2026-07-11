namespace SimPle.Domain.GameHost;

/// <summary>
/// A versioned fact a definition emits about what just happened. Events are the engine's only outward
/// narration — a definition performs no logging, no metrics, and no I/O of its own.
/// <para>
/// Visibility is decided by the definition, not by the transport: an event placed in the public batch is
/// broadcast to every seat and spectator, so anything that would reveal hidden state must go in the private
/// batch addressed to a specific seat.
/// </para>
/// </summary>
public sealed class GameEvent
{
    /// <summary>Stable discriminator, e.g. <c>TokenDrawn</c>. Part of the compatibility contract.</summary>
    public string EventType { get; }

    public int SchemaVersion { get; }

    /// <summary>Serialized event body. Empty for events that carry no data beyond their type.</summary>
    public ReadOnlyMemory<byte> PayloadBytes { get; }

    /// <summary>The seat this event is addressed to, or <see langword="null"/> if it is public.</summary>
    public int? TargetSeat { get; }

    private GameEvent(string eventType, int schemaVersion, ReadOnlyMemory<byte> payloadBytes, int? targetSeat)
    {
        EventType = eventType;
        SchemaVersion = schemaVersion;
        PayloadBytes = payloadBytes;
        TargetSeat = targetSeat;
    }

    public static GameEvent Public(string eventType, int schemaVersion, ReadOnlyMemory<byte> payloadBytes = default) =>
        new(RequireEventType(eventType), RequireSchemaVersion(schemaVersion), payloadBytes, targetSeat: null);

    public static GameEvent Private(string eventType, int schemaVersion, int targetSeat, ReadOnlyMemory<byte> payloadBytes = default)
    {
        if (targetSeat < 0)
            throw new ArgumentOutOfRangeException(nameof(targetSeat), targetSeat, "Target seat must be non-negative.");

        return new GameEvent(RequireEventType(eventType), RequireSchemaVersion(schemaVersion), payloadBytes, targetSeat);
    }

    public bool IsPublic => TargetSeat is null;

    private static string RequireEventType(string eventType) =>
        string.IsNullOrWhiteSpace(eventType)
            ? throw new ArgumentException("EventType must not be empty.", nameof(eventType))
            : eventType;

    private static int RequireSchemaVersion(int schemaVersion) =>
        schemaVersion <= 0
            ? throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "SchemaVersion must be positive.")
            : schemaVersion;
}
