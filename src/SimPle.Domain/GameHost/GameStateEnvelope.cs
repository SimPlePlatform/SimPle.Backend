using System.Security.Cryptography;

namespace SimPle.Domain.GameHost;

/// <summary>
/// The authoritative, server-only snapshot of a match. Module 8 stores it, transports it, and replays it;
/// Module 5 produces and consumes it.
/// <para>
/// <b>This envelope is not safe to hand to a client.</b> It carries the raw serialized state (including every
/// player's hidden information) and <see cref="RngState"/> (from which all future draws are predictable).
/// Clients receive a <see cref="PlayerViewEnvelope"/> instead — never this.
/// </para>
/// <para>
/// <b>The checksum is integrity, not authenticity.</b> It detects a corrupted or truncated byte range. It is
/// <i>not</i> a signature and must never be used as an authorization or anti-tamper control: anyone who can
/// alter the bytes can recompute it. Authority stays with Module 8's authenticated actor/seat binding.
/// </para>
/// </summary>
public sealed class GameStateEnvelope
{
    public string GameSlug { get; }
    public int EngineVersion { get; }
    public int StateSchemaVersion { get; }

    /// <summary>Monotonic counter, incremented only by an accepted command.</summary>
    public int Revision { get; }

    public string RngAlgorithm { get; }

    /// <summary>Server-only. Never project this into a view, a client payload, or a log line.</summary>
    public Pcg32State RngState { get; }

    /// <summary>The exact serialized state bytes the checksum is computed over.</summary>
    public ReadOnlyMemory<byte> StateBytes { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="StateBytes"/>.</summary>
    public string Checksum { get; }

    private GameStateEnvelope(
        string gameSlug,
        int engineVersion,
        int stateSchemaVersion,
        int revision,
        string rngAlgorithm,
        Pcg32State rngState,
        ReadOnlyMemory<byte> stateBytes,
        string checksum)
    {
        GameSlug = gameSlug;
        EngineVersion = engineVersion;
        StateSchemaVersion = stateSchemaVersion;
        Revision = revision;
        RngAlgorithm = rngAlgorithm;
        RngState = rngState;
        StateBytes = stateBytes;
        Checksum = checksum;
    }

    public static GameStateEnvelope Create(
        string gameSlug,
        int engineVersion,
        int stateSchemaVersion,
        int revision,
        Pcg32State rngState,
        ReadOnlySpan<byte> stateBytes,
        string rngAlgorithm = Pcg32.AlgorithmId)
    {
        if (string.IsNullOrWhiteSpace(gameSlug))
            throw new ArgumentException("GameSlug must not be empty.", nameof(gameSlug));
        if (engineVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(engineVersion), engineVersion, "EngineVersion must be positive.");
        if (stateSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(stateSchemaVersion), stateSchemaVersion, "StateSchemaVersion must be positive.");
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");
        if (string.IsNullOrWhiteSpace(rngAlgorithm))
            throw new ArgumentException("RngAlgorithm must not be empty.", nameof(rngAlgorithm));

        // Defensive copy: the checksum is only meaningful if the bytes it was computed over cannot be mutated
        // out from under it by whoever handed us the buffer.
        var copy = stateBytes.ToArray();

        return new GameStateEnvelope(
            gameSlug,
            engineVersion,
            stateSchemaVersion,
            revision,
            rngAlgorithm,
            rngState,
            copy,
            ComputeChecksum(copy));
    }

    /// <summary>
    /// Recomputes the checksum over the carried bytes. A mismatch means the payload was corrupted in storage or
    /// transport and the host must fail closed with <see cref="EngineErrorCode.CorruptState"/> rather than
    /// deserialize it.
    /// </summary>
    public bool ChecksumMatches() => Checksum == ComputeChecksum(StateBytes.Span);

    public static string ComputeChecksum(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
