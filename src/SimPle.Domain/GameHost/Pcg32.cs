namespace SimPle.Domain.GameHost;

/// <summary>
/// The resumable state of a <see cref="Pcg32"/> stream. This is <b>server-only</b>: it lives in the
/// authoritative state envelope and must never reach a player view, a spectator view, a client payload, or a
/// log line. Knowing <see cref="State"/> and <see cref="Inc"/> lets a player predict every future draw.
/// </summary>
/// <param name="State">The LCG state (PCG's <c>state</c>).</param>
/// <param name="Inc">The odd stream selector (PCG's <c>inc</c>). Always odd by construction.</param>
/// <param name="Cursor">Number of 32-bit draws taken since seeding. Bookkeeping/observability only — it does
/// not feed the generator, but it makes an unexpected divergence visible in a golden vector diff.</param>
public readonly record struct Pcg32State(ulong State, ulong Inc, ulong Cursor);

/// <summary>
/// <c>PCG32-v1</c> — the reference <c>pcg_setseq_64_xsh_rr_32</c> generator (64-bit state, 32-bit output,
/// xorshift-high + random-rotation output function), with the published seeding sequence.
/// <para>
/// This is the <b>only</b> source of randomness a game definition may use. Engine code has no ambient RNG, no
/// clock, and no environment access, so identical inputs produce byte-identical output. The generator advances
/// only when the host accepts a command: a rejected or cancelled command discards the advanced state along with
/// everything else it produced.
/// </para>
/// <para>
/// PCG is <b>not</b> cryptographically secure and is not used as such. Its secrecy requirement is met by
/// keeping <see cref="Pcg32State"/> server-only, exactly as the hidden game state itself is.
/// </para>
/// </summary>
public sealed class Pcg32
{
    /// <summary>Versioned algorithm identifier recorded in every state envelope.</summary>
    public const string AlgorithmId = "PCG32-v1";

    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _inc;
    private ulong _cursor;

    private Pcg32(ulong state, ulong inc, ulong cursor)
    {
        _state = state;
        _inc = inc;
        _cursor = cursor;
    }

    /// <summary>
    /// Seeds a fresh stream from the single 128-bit match seed that Module 8 draws from a cryptographic RNG.
    /// <para>
    /// The split is normative and must never change: the <b>high</b> 64 bits become the reference
    /// <c>initstate</c> and the <b>low</b> 64 bits become the reference <c>initseq</c>. Changing the split
    /// changes every future draw for a given seed, which is a determinism break — it requires an engine
    /// version bump, not an edit.
    /// </para>
    /// </summary>
    public static Pcg32 FromMatchSeed(UInt128 matchSeed)
    {
        var initState = (ulong)(matchSeed >> 64);
        var initSeq = (ulong)matchSeed;
        return FromSeedParts(initState, initSeq);
    }

    /// <summary>
    /// Seeds from the two reference parameters directly. Applies the published <c>pcg32_srandom_r</c>
    /// sequence: zero the state, derive the odd stream selector, step once, add <c>initstate</c>, step again.
    /// </summary>
    public static Pcg32 FromSeedParts(ulong initState, ulong initSeq)
    {
        var rng = new Pcg32(state: 0UL, inc: (initSeq << 1) | 1UL, cursor: 0UL);
        rng.Step();
        rng._state += initState;
        rng.Step();

        // The two seeding steps are part of initialization, not draws the game asked for.
        rng._cursor = 0UL;
        return rng;
    }

    /// <summary>Restores a stream from a persisted envelope so a replay continues exactly where it left off.</summary>
    public static Pcg32 Restore(Pcg32State state)
    {
        if ((state.Inc & 1UL) == 0UL)
            throw new ArgumentException("PCG32 stream selector must be odd; the state is corrupt.", nameof(state));

        return new Pcg32(state.State, state.Inc, state.Cursor);
    }

    /// <summary>Captures the current stream position for storage in the server-only state envelope.</summary>
    public Pcg32State Snapshot() => new(_state, _inc, _cursor);

    /// <summary>Number of 32-bit draws taken since seeding.</summary>
    public ulong Cursor => _cursor;

    /// <summary>Draws the next 32-bit value and advances the stream.</summary>
    public uint NextUInt32()
    {
        var value = Step();
        _cursor++;
        return value;
    }

    /// <summary>
    /// Draws a value in <c>[0, exclusiveBound)</c> with no modulo bias, using the reference
    /// <c>pcg32_boundedrand_r</c> rejection loop. The loop is bounded in expectation, not in the worst case,
    /// but each iteration consumes a draw, so it terminates with probability 1 and is fully deterministic for
    /// a given stream position.
    /// </summary>
    public uint NextBounded(uint exclusiveBound)
    {
        if (exclusiveBound == 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveBound), "Bound must be positive.");

        // The reference `-bound % bound`, i.e. 2^32 mod bound. Draws below this would be over-represented by
        // the final modulo, so they are rejected rather than folded in.
        var threshold = unchecked(0u - exclusiveBound) % exclusiveBound;

        while (true)
        {
            var draw = NextUInt32();
            if (draw >= threshold)
                return draw % exclusiveBound;
        }
    }

    /// <summary>Draws a value in <c>[minInclusive, maxExclusive)</c> without bias.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");

        var range = (uint)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + NextBounded(range));
    }

    /// <summary>
    /// In-place unbiased Fisher-Yates shuffle. The iteration order is fixed and the draws come only from this
    /// stream, so the resulting permutation is a pure function of the stream position — which is what makes a
    /// shuffled deck reproducible from a golden vector.
    /// </summary>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = (int)NextBounded((uint)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// One iteration of the reference generator: advance the LCG, then apply the XSH-RR output function to the
    /// <i>previous</i> state. Does not touch the cursor — <see cref="NextUInt32"/> owns that, so the two
    /// seeding steps are not counted as draws.
    /// </summary>
    private uint Step()
    {
        var oldState = _state;
        _state = unchecked((oldState * Multiplier) + _inc);

        var xorshifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
        var rot = (int)(oldState >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }
}
