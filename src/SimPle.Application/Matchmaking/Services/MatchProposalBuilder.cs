using SimPle.Domain.Matchmaking;

namespace SimPle.Application.Matchmaking.Services;

/// <summary>
/// One proposed match: the tickets that will share a group id, a single match request, and one
/// <c>MatchRequestedV1</c>. The anchor is first.
/// </summary>
public sealed record MatchProposal(IReadOnlyList<MatchmakingTicket> Tickets)
{
    public MatchmakingTicket Anchor => Tickets[0];
}

/// <summary>
/// The Phase-1 matchmaking algorithm, as a pure function of a ticket batch and the current time.
///
/// <para>
/// It is deliberately free of the database, the clock, and the worker: every rule the brief cares about — band
/// widening, group compatibility, the four-level tie-break, anti-starvation — is decided here and is therefore
/// provable by a unit test with an injected time, rather than only observable as an emergent property of a
/// concurrent worker run.
/// </para>
///
/// <para>
/// <strong>Anchoring.</strong> The oldest queued ticket anchors a proposal. Together with monotonically widening
/// bands (±100 → ±200 → ±400) that is the whole anti-starvation guarantee: a waiting ticket's band only grows, and
/// it is always considered before newer tickets, so it is never indefinitely skipped while the queue serves easier
/// matches. The 60-second deadline bounds the worst case, which makes <em>expiry</em> — not silent starvation — the
/// terminal outcome.
/// </para>
///
/// <para>
/// <strong>Compatibility is mutual, not anchor-centric.</strong> A group is compatible only when its rating range
/// fits <em>every</em> member's current window, not just the anchor's. Checking only the anchor would let a
/// 60-second-old ±400 anchor drag a 1-second-old ±100 ticket into a match 350 points away from it — the young
/// ticket never agreed to that spread, and its own band is what says so.
/// </para>
/// </summary>
public static class MatchProposalBuilder
{
    /// <summary>
    /// How many of the anchor's nearest eligible partners are considered. Sorted by rating distance from the
    /// anchor first, so the window keeps the most promising candidates, and a pool larger than this cannot make
    /// one cycle's work unbounded (OWASP API4:2023 — algorithmic exhaustion is an availability risk, and the queue
    /// is the one place in this module where an attacker controls the input size).
    /// </summary>
    public const int MaxCandidateWindow = 32;

    /// <summary>
    /// A hard ceiling on group combinations examined per anchor. Reached only for large groups: at the two-player
    /// size every reachable capability profile pins today, <c>need = 1</c> and the search is exhaustive over the
    /// window (at most <see cref="MaxCandidateWindow"/> evaluations), so the result is exactly optimal.
    ///
    /// For a larger supported group the enumeration order is deterministic and starts with the anchor's closest
    /// partners, so a truncated search still returns the same answer on every run — it is bounded, not random.
    /// </summary>
    public const int MaxCombinationsPerAnchor = 20_000;

    /// <summary>
    /// Forms as many non-overlapping proposals as the batch supports. A ticket appears in at most one proposal.
    ///
    /// An anchor that cannot be matched is dropped from <em>this cycle</em> only — its row stays <c>Queued</c>, and
    /// the next cycle re-anchors it with a band that has widened in the meantime. That is why an unmatched oldest
    /// ticket does not block the tickets behind it, and also why it does not lose its place.
    /// </summary>
    public static IReadOnlyList<MatchProposal> BuildProposals(
        IReadOnlyList<MatchmakingTicket> candidates, DateTime nowUtc, BlockedPairs blocked)
    {
        var remaining = candidates
            .Where(t => t.State == MatchmakingTicketState.Queued && !t.IsExpired(nowUtc))
            .OrderBy(t => t.EnqueuedAtUtc).ThenBy(t => t.Id)
            .ToList();

        var proposals = new List<MatchProposal>();

        while (remaining.Count > 0)
        {
            var anchor = remaining[0];   // oldest — the list is kept in anchor order
            remaining.RemoveAt(0);

            var pool = remaining.Where(t => SharesPoolKey(anchor, t)).ToList();
            var proposal = TrySelectGroup(anchor, pool, nowUtc, blocked);
            if (proposal is null) continue;

            proposals.Add(proposal);

            var taken = proposal.Tickets.Select(t => t.Id).ToHashSet();
            remaining.RemoveAll(t => taken.Contains(t.Id));
        }

        return proposals;
    }

    /// <summary>
    /// The best group the anchor can form right now, or null if none is compatible.
    /// </summary>
    public static MatchProposal? TrySelectGroup(
        MatchmakingTicket anchor, IReadOnlyList<MatchmakingTicket> pool, DateTime nowUtc, BlockedPairs blocked)
    {
        var need = anchor.PlayerCount - 1;
        if (need < 1) return null;                      // defensive: enqueue rejects playerCount < 2
        if (anchor.RatingWindow(nowUtc) is null) return null;   // anchor has expired; the sweep owns it

        // Mutual acceptance is a *necessary* condition for any group containing both tickets: the group's range
        // spans both ratings, so each one's window must already reach the other. Filtering on it first is what
        // keeps the combination search small.
        //
        // The block filter sits here, before selection, rather than as a veto on a finished group — see BlockedPairs.
        var eligible = pool
            .Where(t => SharesPoolKey(anchor, t)
                        && !blocked.AreBlocked(anchor.UserId, t.UserId)
                        && MutuallyAcceptable(anchor, t, nowUtc))
            .OrderBy(t => Math.Abs(t.Rating - anchor.Rating))
            .ThenBy(t => t.EnqueuedAtUtc)
            .ThenBy(t => t.Id)
            .Take(MaxCandidateWindow)
            .ToList();

        if (eligible.Count < need) return null;

        MatchmakingTicket[]? best = null;
        var examined = 0;

        foreach (var combination in Combinations(eligible, need))
        {
            if (++examined > MaxCombinationsPerAnchor) break;

            var group = new MatchmakingTicket[need + 1];
            group[0] = anchor;
            combination.CopyTo(group, 1);

            // Pairwise-with-the-anchor is already excluded above, but a group of three or more must also be free of
            // blocks *among the non-anchor members* — two players who blocked each other must not be seated together
            // merely because they each get along with the anchor.
            if (HasBlockedPair(group, blocked)) continue;

            if (!IsCompatible(group, nowUtc)) continue;
            if (best is null || Compare(group, best, anchor) < 0) best = group;
        }

        return best is null ? null : new MatchProposal(best);
    }

    private static bool HasBlockedPair(IReadOnlyList<MatchmakingTicket> group, BlockedPairs blocked)
    {
        for (var i = 0; i < group.Count; i++)
        {
            for (var j = i + 1; j < group.Count; j++)
            {
                if (blocked.AreBlocked(group[i].UserId, group[j].UserId)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Exact-match candidate pool key. Phase 1 is same-region, and every field is a ticket <em>snapshot</em>, so a
    /// profile or setting that changes mid-queue can never silently re-pool a waiting ticket.
    /// </summary>
    private static bool SharesPoolKey(MatchmakingTicket a, MatchmakingTicket b) =>
        string.Equals(a.GameSlug, b.GameSlug, StringComparison.Ordinal)
        && a.CapabilityVersion == b.CapabilityVersion
        && string.Equals(a.Mode, b.Mode, StringComparison.Ordinal)
        && a.PlayerCount == b.PlayerCount
        && string.Equals(a.TimeControlId, b.TimeControlId, StringComparison.Ordinal)
        && a.Rated == b.Rated
        && string.Equals(a.ResolvedRegion, b.ResolvedRegion, StringComparison.Ordinal);

    /// <summary>Each ticket's current band reaches the other's rating. Necessary, not sufficient.</summary>
    private static bool MutuallyAcceptable(MatchmakingTicket a, MatchmakingTicket b, DateTime nowUtc)
    {
        var wa = a.RatingWindow(nowUtc);
        var wb = b.RatingWindow(nowUtc);
        if (wa is null || wb is null) return false;

        return b.Rating >= wa.Value.Low && b.Rating <= wa.Value.High
            && a.Rating >= wb.Value.Low && a.Rating <= wb.Value.High;
    }

    /// <summary>
    /// The group's rating range fits inside <em>every</em> member's current window. This is the sufficient
    /// condition, and it is strictly stronger than pairwise mutual acceptance once a group exceeds two members:
    /// three tickets can each accept the other two individually while the outer two still straddle the middle
    /// one's band.
    /// </summary>
    private static bool IsCompatible(IReadOnlyList<MatchmakingTicket> group, DateTime nowUtc)
    {
        var low = int.MaxValue;
        var high = int.MinValue;
        foreach (var t in group)
        {
            if (t.Rating < low) low = t.Rating;
            if (t.Rating > high) high = t.Rating;
        }

        foreach (var t in group)
        {
            var window = t.RatingWindow(nowUtc);
            if (window is null) return false;
            if (low < window.Value.Low || high > window.Value.High) return false;
        }

        return true;
    }

    /// <summary>
    /// The brief's four-level tie-break, in order: smallest rating range, then lowest total distance from the
    /// anchor, then earliest creation time, then lowest ticket id. Negative means <paramref name="x"/> wins.
    ///
    /// Levels 3 and 4 compare the members' creation times and ids as ascending sequences. Two distinct groups
    /// always differ in at least one member id, so level 4 is a total order — the selection is fully deterministic
    /// and can never depend on enumeration order or wall-clock luck.
    /// </summary>
    private static int Compare(
        IReadOnlyList<MatchmakingTicket> x, IReadOnlyList<MatchmakingTicket> y, MatchmakingTicket anchor)
    {
        var byRange = RatingRange(x).CompareTo(RatingRange(y));
        if (byRange != 0) return byRange;

        var byDistance = DistanceFrom(x, anchor).CompareTo(DistanceFrom(y, anchor));
        if (byDistance != 0) return byDistance;

        var xTimes = x.Select(t => t.EnqueuedAtUtc).OrderBy(t => t).ToArray();
        var yTimes = y.Select(t => t.EnqueuedAtUtc).OrderBy(t => t).ToArray();
        for (var i = 0; i < xTimes.Length; i++)
        {
            var byTime = xTimes[i].CompareTo(yTimes[i]);
            if (byTime != 0) return byTime;
        }

        var xIds = x.Select(t => t.Id).OrderBy(t => t).ToArray();
        var yIds = y.Select(t => t.Id).OrderBy(t => t).ToArray();
        for (var i = 0; i < xIds.Length; i++)
        {
            var byId = xIds[i].CompareTo(yIds[i]);
            if (byId != 0) return byId;
        }

        return 0;
    }

    private static int RatingRange(IReadOnlyList<MatchmakingTicket> group) =>
        group.Max(t => t.Rating) - group.Min(t => t.Rating);

    private static long DistanceFrom(IReadOnlyList<MatchmakingTicket> group, MatchmakingTicket anchor) =>
        group.Sum(t => (long)Math.Abs(t.Rating - anchor.Rating));

    /// <summary>
    /// Every <paramref name="need"/>-sized combination of <paramref name="source"/>, in ascending index order.
    /// Because <paramref name="source"/> arrives sorted by distance from the anchor, the enumeration visits the
    /// closest partners first — so truncating it at the combination budget drops the least promising groups, and
    /// drops the same ones every run.
    /// </summary>
    private static IEnumerable<MatchmakingTicket[]> Combinations(IReadOnlyList<MatchmakingTicket> source, int need)
    {
        var indices = new int[need];
        for (var i = 0; i < need; i++) indices[i] = i;

        while (true)
        {
            var combination = new MatchmakingTicket[need];
            for (var i = 0; i < need; i++) combination[i] = source[indices[i]];
            yield return combination;

            // Advance the rightmost index that still has room, then repack everything after it.
            var pivot = need - 1;
            while (pivot >= 0 && indices[pivot] == source.Count - need + pivot) pivot--;
            if (pivot < 0) yield break;

            indices[pivot]++;
            for (var i = pivot + 1; i < need; i++) indices[i] = indices[i - 1] + 1;
        }
    }
}
