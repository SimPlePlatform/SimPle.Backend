using System.Reflection;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.Support;

/// <summary>
/// Test-only construction helpers for <see cref="GameStateEnvelope"/> shapes the public API deliberately
/// cannot produce. <see cref="GameStateEnvelope.Create"/> always recomputes a self-consistent checksum from the
/// bytes it is given, which is correct for production code but means it cannot express "bytes that were
/// corrupted after the checksum was computed" — exactly the fail-closed path <c>Engine.CorruptState</c> exists
/// to catch. Reflection into the private constructor is the narrowest way to build that scenario without adding
/// a checksum-bypassing factory to the production type.
/// </summary>
internal static class GameStateEnvelopeTestFactory
{
    public static GameStateEnvelope WithTamperedChecksum(GameStateEnvelope source)
    {
        var ctor = typeof(GameStateEnvelope)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single();

        var bogusChecksum = new string('0', source.Checksum.Length) == source.Checksum
            ? new string('f', source.Checksum.Length)
            : new string('0', source.Checksum.Length);

        return (GameStateEnvelope)ctor.Invoke(new object?[]
        {
            source.GameSlug,
            source.EngineVersion,
            source.StateSchemaVersion,
            source.Revision,
            source.RngAlgorithm,
            source.RngState,
            source.StateBytes,
            bogusChecksum,
        });
    }
}
