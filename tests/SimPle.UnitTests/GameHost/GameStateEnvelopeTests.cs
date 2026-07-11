using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The state envelope is the authoritative, server-only record of a match. Two properties matter here: the
/// checksum genuinely covers the exact bytes (so corruption in storage or transport is detectable), and the
/// envelope cannot be mutated out from under its own checksum.
/// </summary>
public sealed class GameStateEnvelopeTests
{
    private static readonly Pcg32State AnyRngState = Pcg32.FromSeedParts(42UL, 54UL).Snapshot();

    private static GameStateEnvelope Create(byte[] stateBytes, int revision = 0) => GameStateEnvelope.Create(
        gameSlug: "hidden-token-draft",
        engineVersion: 1,
        stateSchemaVersion: 1,
        revision: revision,
        rngState: AnyRngState,
        stateBytes: stateBytes);

    [Fact]
    public void Create_ComputesSha256OverTheExactStateBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"turn":0}""");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var envelope = Create(bytes);

        envelope.Checksum.Should().Be(expected);
        envelope.ChecksumMatches().Should().BeTrue();
    }

    [Fact]
    public void Checksum_ChangesWhenASingleByteChanges()
    {
        var a = Create(Encoding.UTF8.GetBytes("""{"turn":0}"""));
        var b = Create(Encoding.UTF8.GetBytes("""{"turn":1}"""));

        b.Checksum.Should().NotBe(a.Checksum);
    }

    [Fact]
    public void Create_CopiesTheCallersBuffer_SoLaterMutationCannotDesyncTheChecksum()
    {
        // Without the defensive copy, a caller reusing a pooled buffer would silently invalidate the checksum
        // of an envelope it had already handed off — and the corruption would surface as a fail-closed error on
        // some later replay, far from the cause.
        var buffer = Encoding.UTF8.GetBytes("""{"turn":0}""");
        var envelope = Create(buffer);

        buffer[2] = (byte)'X';

        envelope.ChecksumMatches().Should().BeTrue("the envelope must own its bytes");
        envelope.StateBytes.ToArray().Should().NotEqual(buffer);
    }

    [Fact]
    public void RngState_IsCarried_ForReplayButIsServerOnly()
    {
        // The presence of the RNG state here is exactly why this envelope must never be handed to a client:
        // it makes every future draw predictable. The redaction boundary is PlayerViewEnvelope, not this type.
        var envelope = Create(Encoding.UTF8.GetBytes("{}"));

        envelope.RngState.Should().Be(AnyRngState);
        envelope.RngAlgorithm.Should().Be(Pcg32.AlgorithmId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositiveEngineVersion_Throws(int engineVersion)
    {
        var create = () => GameStateEnvelope.Create(
            "slug", engineVersion, 1, 0, AnyRngState, ReadOnlySpan<byte>.Empty);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithNonPositiveStateSchemaVersion_Throws()
    {
        var create = () => GameStateEnvelope.Create("slug", 1, 0, 0, AnyRngState, ReadOnlySpan<byte>.Empty);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithNegativeRevision_Throws()
    {
        var create = () => GameStateEnvelope.Create("slug", 1, 1, -1, AnyRngState, ReadOnlySpan<byte>.Empty);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithEmptySlug_Throws()
    {
        var create = () => GameStateEnvelope.Create(" ", 1, 1, 0, AnyRngState, ReadOnlySpan<byte>.Empty);

        create.Should().Throw<ArgumentException>();
    }
}
