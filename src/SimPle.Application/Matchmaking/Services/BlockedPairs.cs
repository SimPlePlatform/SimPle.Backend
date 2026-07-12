namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// The block relationships (M3) among one worker batch's ticket owners, as an unordered-pair lookup.
///
/// <para>
/// Blocks are checked <strong>before queue assignment</strong>, not after a proposal is formed. That distinction
/// matters: rejecting a formed group would leave the same two tickets to be re-proposed on every subsequent cycle,
/// so a single blocked pair at the head of the queue could stall matching for both of them until they timed out.
/// Excluding the pair from the candidate pool instead lets each ticket match with somebody else immediately.
/// </para>
///
/// <para>
/// A block is symmetric in its effect here — it does not matter who blocked whom, the two must not be placed in a
/// match proposal together — so the key is order-independent.
/// </para>
/// </summary>
public sealed class BlockedPairs
{
    public static readonly BlockedPairs None = new(Array.Empty<(Guid, Guid)>());

    private readonly HashSet<(Guid, Guid)> _pairs;

    public BlockedPairs(IEnumerable<(Guid A, Guid B)> pairs)
    {
        _pairs = pairs.Select(p => Normalize(p.A, p.B)).ToHashSet();
    }

    public bool AreBlocked(Guid a, Guid b) => _pairs.Contains(Normalize(a, b));

    public int Count => _pairs.Count;

    private static (Guid, Guid) Normalize(Guid a, Guid b) =>
        a.CompareTo(b) <= 0 ? (a, b) : (b, a);
}
