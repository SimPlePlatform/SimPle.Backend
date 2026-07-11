using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// <c>PCG32-v1</c> is the single source of randomness in the game host, so every determinism guarantee in the
/// module — replay, golden vectors, dispute resolution — reduces to this generator producing the same stream
/// on every machine and every run.
/// <para>
/// The reference-vector test is the important one: it pins the implementation to the <i>published</i> PCG32
/// output, not merely to "whatever it did last time". A refactor that stays self-consistent but drifts from
/// the reference would silently invalidate every stored match, and only this test would catch it.
/// </para>
/// </summary>
public sealed class Pcg32Tests
{
    /// <summary>
    /// The canonical output of <c>pcg32-demo</c> seeded with <c>pcg32_srandom_r(&amp;rng, 42u, 54u)</c>.
    /// These constants come from the PCG reference implementation and must never be "fixed" to match our code:
    /// if this test fails, the implementation is wrong, not the vector.
    /// </summary>
    private static readonly uint[] ReferenceStream4254 =
    [
        0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e,
    ];

    [Fact]
    public void NextUInt32_WithReferenceSeed_MatchesPublishedPcg32Vector()
    {
        var rng = Pcg32.FromSeedParts(initState: 42UL, initSeq: 54UL);

        var actual = Enumerable.Range(0, ReferenceStream4254.Length).Select(_ => rng.NextUInt32()).ToArray();

        actual.Should().Equal(ReferenceStream4254);
    }

    [Fact]
    public void FromMatchSeed_SplitsHigh64ToInitStateAndLow64ToInitSeq()
    {
        // The split is normative: high 64 -> initstate, low 64 -> initseq. If it ever changed, every seed
        // would deal a different game, so this test pins it against the reference parameters directly.
        var matchSeed = ((UInt128)42UL << 64) | 54UL;

        var fromMatchSeed = Pcg32.FromMatchSeed(matchSeed);
        var fromParts = Pcg32.FromSeedParts(42UL, 54UL);

        Draw(fromMatchSeed, 6).Should().Equal(Draw(fromParts, 6));
        Draw(Pcg32.FromMatchSeed(matchSeed), 6).Should().Equal(ReferenceStream4254);
    }

    [Fact]
    public void NextUInt32_SameSeed_IsReproducibleAcrossOneHundredFreshStreams()
    {
        // The module claims byte-determinism for identical inputs; 100 repeats is the spec's stated bar.
        var expected = Draw(Pcg32.FromSeedParts(7UL, 11UL), 32);

        for (var run = 0; run < 100; run++)
            Draw(Pcg32.FromSeedParts(7UL, 11UL), 32).Should().Equal(expected, "run {0} must reproduce the stream", run);
    }

    [Fact]
    public void NextUInt32_DifferentStreamSelector_ProducesIndependentSequences()
    {
        var a = Draw(Pcg32.FromSeedParts(42UL, 54UL), 16);
        var b = Draw(Pcg32.FromSeedParts(42UL, 55UL), 16);

        a.Should().NotEqual(b);
    }

    [Fact]
    public void Cursor_CountsDrawsOnly_NotTheTwoSeedingSteps()
    {
        var rng = Pcg32.FromSeedParts(42UL, 54UL);
        rng.Cursor.Should().Be(0, "seeding steps are initialization, not draws the game asked for");

        rng.NextUInt32();
        rng.NextUInt32();

        rng.Cursor.Should().Be(2);
    }

    [Fact]
    public void Restore_FromSnapshot_ResumesTheExactStream()
    {
        // This is the replay path: Module 8 stores the snapshot in the state envelope and rehydrates it later.
        var original = Pcg32.FromSeedParts(42UL, 54UL);
        Draw(original, 3);
        var snapshot = original.Snapshot();

        var expectedContinuation = Draw(original, 5);
        var restored = Pcg32.Restore(snapshot);

        Draw(restored, 5).Should().Equal(expectedContinuation);
        restored.Snapshot().Cursor.Should().Be(8);
    }

    [Fact]
    public void Restore_WithEvenStreamSelector_IsRejectedAsCorrupt()
    {
        // PCG requires an odd increment; an even one means the persisted state was corrupted or forged.
        var corrupt = new Pcg32State(State: 123UL, Inc: 8UL, Cursor: 0UL);

        var restore = () => Pcg32.Restore(corrupt);

        restore.Should().Throw<ArgumentException>().WithMessage("*odd*");
    }

    [Fact]
    public void NextBounded_StaysInRange_AndIsDeterministic()
    {
        var rng = Pcg32.FromSeedParts(1UL, 2UL);
        var draws = Enumerable.Range(0, 500).Select(_ => rng.NextBounded(6)).ToArray();

        draws.Should().OnlyContain(d => d < 6);
        draws.Should().Contain(0).And.Contain(5, "a bounded draw must be able to reach both ends of its range");

        var replay = Pcg32.FromSeedParts(1UL, 2UL);
        Enumerable.Range(0, 500).Select(_ => replay.NextBounded(6)).Should().Equal(draws);
    }

    [Fact]
    public void NextBounded_WithZeroBound_Throws()
    {
        var rng = Pcg32.FromSeedParts(1UL, 2UL);

        var draw = () => rng.NextBounded(0);

        draw.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NextInt_RespectsInclusiveAndExclusiveBounds()
    {
        var rng = Pcg32.FromSeedParts(9UL, 9UL);

        var draws = Enumerable.Range(0, 500).Select(_ => rng.NextInt(-3, 4)).ToArray();

        draws.Should().OnlyContain(d => d >= -3 && d < 4);
    }

    [Fact]
    public void NextInt_WithInvertedRange_Throws()
    {
        var rng = Pcg32.FromSeedParts(9UL, 9UL);

        var draw = () => rng.NextInt(5, 5);

        draw.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Shuffle_WithSameSeed_ProducesTheSamePermutation()
    {
        // A shuffled deck is the archetypal hidden-information setup; it has to be reproducible from the seed
        // alone, or a match cannot be replayed from its golden vector.
        var first = Enumerable.Range(0, 20).ToList();
        var second = Enumerable.Range(0, 20).ToList();

        Pcg32.FromSeedParts(42UL, 54UL).Shuffle(first);
        Pcg32.FromSeedParts(42UL, 54UL).Shuffle(second);

        first.Should().Equal(second);
        first.Should().NotEqual(Enumerable.Range(0, 20), "a 20-element shuffle must actually permute");
        first.Should().BeEquivalentTo(Enumerable.Range(0, 20), "a shuffle permutes, it never adds or drops");
    }

    [Fact]
    public void Shuffle_WithDifferentSeed_ProducesADifferentPermutation()
    {
        var a = Enumerable.Range(0, 20).ToList();
        var b = Enumerable.Range(0, 20).ToList();

        Pcg32.FromSeedParts(42UL, 54UL).Shuffle(a);
        Pcg32.FromSeedParts(99UL, 54UL).Shuffle(b);

        a.Should().NotEqual(b);
    }

    [Fact]
    public void AlgorithmId_IsTheVersionedIdentifierRecordedInEveryEnvelope()
    {
        Pcg32.AlgorithmId.Should().Be("PCG32-v1");
    }

    private static uint[] Draw(Pcg32 rng, int count) =>
        Enumerable.Range(0, count).Select(_ => rng.NextUInt32()).ToArray();
}
