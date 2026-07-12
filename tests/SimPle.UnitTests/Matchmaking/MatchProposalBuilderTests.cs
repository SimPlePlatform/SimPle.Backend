using FluentAssertions;
using SimPle.Application.Matchmaking.Services;
using SimPle.Domain.Matchmaking;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The Phase-1 matchmaking algorithm (slice 6C): pool keys, band widening, mutual compatibility, the four-level
/// tie-break, blocks, and anti-starvation.
///
/// <para>
/// Every test drives an explicit clock. That is not a style preference — the bands are defined by ticket <em>age</em>
/// (±100 → ±200 → ±400 at 15s/30s), so a wall-clock test could not distinguish "the band widened correctly" from
/// "the test happened to take long enough", and an off-by-one at a boundary would be invisible (brief Risk #8).
/// </para>
/// </summary>
public class MatchProposalBuilderTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private static IReadOnlyList<MatchProposal> Build(
        IReadOnlyList<MatchmakingTicket> tickets, DateTime nowUtc, BlockedPairs? blocked = null) =>
        MatchProposalBuilder.BuildProposals(tickets, nowUtc, blocked ?? BlockedPairs.None);

    // ── Pool key ─────────────────────────────────────────────────────────────

    [Fact]
    public void TwoIdenticalTicketsArePaired()
    {
        var a = TicketFactory.Queued(T0);
        var b = TicketFactory.Queued(T0);

        var proposals = Build(new[] { a, b }, T0);

        proposals.Should().ContainSingle();
        proposals[0].Tickets.Should().HaveCount(2);
        proposals[0].Tickets.Select(t => t.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Theory]
    [InlineData("gameSlug")]
    [InlineData("mode")]
    [InlineData("timeControlId")]
    [InlineData("rated")]
    [InlineData("resolvedRegion")]
    [InlineData("playerCount")]
    public void TicketsThatDifferOnAnyPoolKeyFieldAreNeverPaired(string differingField)
    {
        // The pool key is an *exact* match on every field. A near-miss is not a worse match — it is a different
        // game entirely, and pairing across it would produce a match neither player asked for.
        var a = TicketFactory.Queued(T0);
        var b = differingField switch
        {
            "gameSlug" => TicketFactory.Queued(T0, gameSlug: "checkers"),
            "mode" => TicketFactory.Queued(T0, mode: "ranked"),
            "timeControlId" => TicketFactory.Queued(T0, timeControlId: "rapid-10-0"),
            "rated" => TicketFactory.Queued(T0, rated: true),
            "resolvedRegion" => TicketFactory.Queued(T0, resolvedRegion: "us-east"),
            "playerCount" => TicketFactory.Queued(T0, playerCount: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(differingField)),
        };

        Build(new[] { a, b }, T0).Should().BeEmpty();
    }

    // ── Compatibility is mutual, not anchor-centric ──────────────────────────

    [Fact]
    public void AWideBandedAnchorCannotDragAYoungTicketBeyondItsOwnNarrowBand()
    {
        // The bug this exists to catch: checking only the anchor's window. A 30-second-old anchor accepts ±400, so
        // a naive implementation happily pairs it with a ticket 300 points away — but that ticket enqueued one
        // second ago and has only agreed to ±100. It never consented to that spread, and its own band says so.
        var anchor = TicketFactory.Queued(T0, rating: 1200);
        var young = TicketFactory.Queued(T0.AddSeconds(29), rating: 1500);

        var now = T0.AddSeconds(30);   // anchor is 30s old (±400); young is 1s old (±100)

        anchor.CurrentBand(now).Should().Be(400);
        young.CurrentBand(now).Should().Be(100);

        Build(new[] { anchor, young }, now).Should().BeEmpty();
    }

    [Fact]
    public void OnceBothBandsReachEachOtherThePairIsFormed()
    {
        var anchor = TicketFactory.Queued(T0, rating: 1200);
        var other = TicketFactory.Queued(T0, rating: 1350);

        // At 0s both are ±100: 150 apart, so neither reaches the other.
        Build(new[] { anchor, other }, T0).Should().BeEmpty();

        // At 15s both are ±200, which now spans the 150-point gap from both sides.
        Build(new[] { anchor, other }, T0.AddSeconds(15)).Should().ContainSingle();
    }

    [Fact]
    public void AGroupOfThreeIsRejectedWhenTwoNonAnchorMembersAreTooFarApartFromEachOther()
    {
        // The gap that the whole-group range check exists to close, and the reason the candidate filter alone is not
        // enough. The filter only asks "does this ticket mutually accept the *anchor*" — so an anchor sitting in the
        // middle happily admits one candidate 300 below it and another 300 above, while those two are 600 apart and
        // neither would ever have accepted the other. Checking the group's full range against every member's window
        // is what rejects it.
        var anchor = TicketFactory.Queued(T0, rating: 1200, playerCount: 3);
        var low = TicketFactory.Queued(T0, rating: 900, playerCount: 3);
        var high = TicketFactory.Queued(T0, rating: 1500, playerCount: 3);

        var now = T0.AddSeconds(30);   // all three are ±400

        // The anchor reaches both, in both directions — so both survive the candidate filter.
        anchor.RatingWindow(now).Should().Be((800, 1600));
        low.RatingWindow(now).Should().Be((500, 1300));
        high.RatingWindow(now).Should().Be((1100, 1900));

        // But the group's range is 900..1500, and `low` agreed to nothing above 1300.
        Build(new[] { anchor, low, high }, now).Should().BeEmpty();
    }

    [Fact]
    public void AGroupOfThreeFormsOnceItsFullRangeFitsEveryMembersWindow()
    {
        // The same shape as above with `high` pulled in to 1300: the range becomes 900..1300, which now fits inside
        // all three windows. Proves the rejection above is the range check doing its job, not some unrelated
        // incompatibility in the three-player path.
        var anchor = TicketFactory.Queued(T0, rating: 1200, playerCount: 3);
        var low = TicketFactory.Queued(T0, rating: 900, playerCount: 3);
        var high = TicketFactory.Queued(T0, rating: 1300, playerCount: 3);

        var proposals = Build(new[] { anchor, low, high }, T0.AddSeconds(30));

        // Which of the three anchors is decided by the Guid tie-break here (they share a timestamp) and does not
        // matter: the group is compatible whichever one leads, which is the property being asserted.
        proposals.Should().ContainSingle();
        proposals[0].Tickets.Should().HaveCount(3);
    }

    // ── Tie-break ────────────────────────────────────────────────────────────

    // Each tie-break test enqueues its anchor one second earlier than the candidates. Without that, all three
    // tickets would share a timestamp and the anchor would be chosen by the Guid tie-break — i.e. at random — so the
    // test would be asserting against a candidate that is sometimes the anchor itself. Evaluated at T0+5s, every
    // ticket is still inside the first band, so the bands stay equal and only the tie-break under test can decide.

    [Fact]
    public void TheSmallestRatingRangeWins()
    {
        var anchor = TicketFactory.Queued(T0, rating: 1200);
        var near = TicketFactory.Queued(T0.AddSeconds(1), rating: 1220);
        var far = TicketFactory.Queued(T0.AddSeconds(1), rating: 1280);

        var proposals = Build(new[] { anchor, near, far }, T0.AddSeconds(5));

        // Both are inside the anchor's ±100, and both reach back. The nearer one makes the tighter match.
        proposals.Should().ContainSingle();
        proposals[0].Anchor.Id.Should().Be(anchor.Id);
        proposals[0].Tickets.Select(t => t.Id).Should().Contain(near.Id);
        proposals[0].Tickets.Select(t => t.Id).Should().NotContain(far.Id);
    }

    [Fact]
    public void OnAnIdenticalRange_TheEarlierTicketWins()
    {
        // Same rating, so range and distance are both tied for either candidate. Level 3 — earliest creation —
        // decides, and it is what keeps the queue fair: the player who has been waiting longer is served first.
        var anchor = TicketFactory.Queued(T0, rating: 1200);
        var early = TicketFactory.Queued(T0.AddSeconds(1), rating: 1250);
        var late = TicketFactory.Queued(T0.AddSeconds(2), rating: 1250);

        var proposals = Build(new[] { anchor, late, early }, T0.AddSeconds(5));   // input deliberately out of order

        proposals.Should().ContainSingle();
        proposals[0].Anchor.Id.Should().Be(anchor.Id);
        proposals[0].Tickets.Select(t => t.Id).Should().Contain(early.Id);
        proposals[0].Tickets.Select(t => t.Id).Should().NotContain(late.Id);
    }

    [Fact]
    public void OnAnIdenticalRangeAndTime_TheLowestTicketIdWins_SoTheChoiceIsAlwaysDeterministic()
    {
        // Rating, range, distance, and creation time all tie. Only level 4 separates them — and it must, or the
        // selection would depend on enumeration order, which is exactly how two workers reading the same rows in a
        // different order end up disagreeing about who plays whom.
        var anchor = TicketFactory.Queued(T0, rating: 1200);
        var x = TicketFactory.Queued(T0.AddSeconds(1), rating: 1250);
        var y = TicketFactory.Queued(T0.AddSeconds(1), rating: 1250);

        var expected = x.Id.CompareTo(y.Id) < 0 ? x.Id : y.Id;

        // Same input, both orderings — the answer must not move.
        var forwards = Build(new[] { anchor, x, y }, T0.AddSeconds(5));
        var backwards = Build(new[] { anchor, y, x }, T0.AddSeconds(5));

        forwards[0].Tickets.Select(t => t.Id).Should().Contain(expected);
        backwards[0].Tickets.Select(t => t.Id).Should().Contain(expected);
    }

    // ── Anchoring and anti-starvation ────────────────────────────────────────

    [Fact]
    public void TheOldestTicketAnchorsTheProposal()
    {
        var oldest = TicketFactory.Queued(T0, rating: 1200);
        var newer = TicketFactory.Queued(T0.AddSeconds(5), rating: 1200);
        var newest = TicketFactory.Queued(T0.AddSeconds(9), rating: 1200);

        var proposals = Build(new[] { newest, newer, oldest }, T0.AddSeconds(10));

        // Three identical tickets, so only one pair can form — and it must contain the one that has waited longest.
        proposals.Should().ContainSingle();
        proposals[0].Anchor.Id.Should().Be(oldest.Id);
        proposals[0].Tickets.Select(t => t.Id).Should().Contain(newer.Id);
    }

    [Fact]
    public void AnUnmatchableAnchorDoesNotBlockTheTicketsBehindIt()
    {
        // Anti-starvation from the *other* side: a lonely oldest ticket must not hold up a pair that can match. It
        // stays Queued (this only drops it from *this cycle*) and is re-anchored next cycle with a wider band.
        var lonely = TicketFactory.Queued(T0, gameSlug: "checkers");
        var a = TicketFactory.Queued(T0.AddSeconds(1));
        var b = TicketFactory.Queued(T0.AddSeconds(2));

        var proposals = Build(new[] { lonely, a, b }, T0.AddSeconds(3));

        proposals.Should().ContainSingle();
        proposals[0].Tickets.Select(t => t.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id });
        proposals.SelectMany(p => p.Tickets).Should().NotContain(lonely);
    }

    [Fact]
    public void ATicketAppearsInAtMostOneProposal()
    {
        var tickets = Enumerable.Range(0, 4).Select(_ => TicketFactory.Queued(T0)).ToList();

        var proposals = Build(tickets, T0);

        proposals.Should().HaveCount(2);

        var used = proposals.SelectMany(p => p.Tickets.Select(t => t.Id)).ToList();
        used.Should().HaveCount(4);
        used.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AnOddTicketIsLeftForTheNextCycleRatherThanForcedIntoAGroup()
    {
        var tickets = Enumerable.Range(0, 3).Select(_ => TicketFactory.Queued(T0)).ToList();

        var proposals = Build(tickets, T0);

        proposals.Should().ContainSingle();
        proposals[0].Tickets.Should().HaveCount(2);
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnExpiredTicketIsNeverProposed_TheSweepOwnsItNow()
    {
        var expired = TicketFactory.Queued(T0);
        var fresh = TicketFactory.Queued(T0.AddSeconds(59));

        var now = T0.AddSeconds(60);   // the first ticket is exactly at its deadline, which is already expired

        expired.IsExpired(now).Should().BeTrue();

        Build(new[] { expired, fresh }, now).Should().BeEmpty();
    }

    [Fact]
    public void AClaimedTicketIsNeverReproposed()
    {
        // The builder only ever considers Queued rows. A ticket another worker already claimed is not a candidate,
        // which is the first of the two defences against double assignment (the partial unique index is the one
        // that actually guarantees it).
        var claimed = TicketFactory.Queued(T0);
        claimed.Claim("worker-1", T0);

        var queued = TicketFactory.Queued(T0);

        Build(new[] { claimed, queued }, T0).Should().BeEmpty();
    }

    // ── Blocks ───────────────────────────────────────────────────────────────

    [Fact]
    public void TwoUsersWithABlockBetweenThemAreNeverProposedTogether()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var a = TicketFactory.Queued(T0, userId: alice);
        var b = TicketFactory.Queued(T0, userId: bob);

        var blocked = new BlockedPairs(new[] { (alice, bob) });

        Build(new[] { a, b }, T0, blocked).Should().BeEmpty();
    }

    [Fact]
    public void ABlockIsSymmetric_ItDoesNotMatterWhoBlockedWhom()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var a = TicketFactory.Queued(T0, userId: alice);
        var b = TicketFactory.Queued(T0, userId: bob);

        // Recorded in the opposite direction to the pairing order.
        var blocked = new BlockedPairs(new[] { (bob, alice) });

        Build(new[] { a, b }, T0, blocked).Should().BeEmpty();
    }

    [Fact]
    public void ABlockedPairIsExcludedFromTheCandidatePool_SoBothStillMatchWithSomeoneElse()
    {
        // The reason blocks are filtered *before* selection rather than vetoing a finished group: if a blocked pair
        // could still be proposed and then rejected, the same two tickets would be re-proposed every cycle and both
        // players would sit at the head of the queue until they timed out.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var a = TicketFactory.Queued(T0, userId: alice);
        var b = TicketFactory.Queued(T0.AddSeconds(1), userId: bob);
        var c = TicketFactory.Queued(T0.AddSeconds(2));
        var d = TicketFactory.Queued(T0.AddSeconds(3));

        var blocked = new BlockedPairs(new[] { (alice, bob) });

        var proposals = Build(new[] { a, b, c, d }, T0.AddSeconds(4), blocked);

        proposals.Should().HaveCount(2);

        // Nobody was starved: all four matched, just not with the person they blocked.
        proposals.SelectMany(p => p.Tickets.Select(t => t.Id)).Should().HaveCount(4);

        foreach (var proposal in proposals)
        {
            var users = proposal.Tickets.Select(t => t.UserId).ToList();
            users.Should().NotBeEquivalentTo(new[] { alice, bob });
        }
    }

    [Fact]
    public void ABlockBetweenTwoNonAnchorMembersStillRejectsTheGroup()
    {
        // A three-player group where the anchor gets along with both, but the other two blocked each other. Filtering
        // only against the anchor would seat them together.
        var anchorUser = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var anchor = TicketFactory.Queued(T0, userId: anchorUser, playerCount: 3);
        var a = TicketFactory.Queued(T0, userId: alice, playerCount: 3);
        var b = TicketFactory.Queued(T0, userId: bob, playerCount: 3);

        var blocked = new BlockedPairs(new[] { (alice, bob) });

        Build(new[] { anchor, a, b }, T0, blocked).Should().BeEmpty();

        // Sanity: without the block the same three do form a group, so the rejection above is the block's doing and
        // not some unrelated incompatibility.
        Build(new[] { anchor, a, b }, T0, BlockedPairs.None).Should().ContainSingle();
    }
}
