using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Matchmaking;
using SimPle.UnitTests.Lobbies;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The band boundaries the brief makes mandatory (Risk #8): exactly 15s, 30s, and 60s, each probed just below, at,
/// and just above. These are the tests a wall-clock suite cannot write — a real clock cannot be parked on the
/// instant a boundary flips, so an off-by-one in the comparison would pass a wall-clock test forever.
///
/// The boundaries are half-open: [0,15) is +/-100, [15,30) is +/-200, [30,60) is +/-400, and >= 60 is expired.
/// "At" therefore always means the NEW band, never the old one.
/// </summary>
public class MatchmakingBandTests
{
    private static readonly DateTime T0 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private static (MatchmakingTicket Ticket, FakeTimeProvider Clock) Enqueued()
    {
        var clock = new FakeTimeProvider(T0);
        var ticket = TicketFactory.Queued(clock.GetUtcNow().UtcDateTime);
        return (ticket, clock);
    }

    private static int? BandAfter(TimeSpan elapsed)
    {
        var (ticket, clock) = Enqueued();
        clock.Advance(elapsed);
        return ticket.CurrentBand(clock.GetUtcNow().UtcDateTime);
    }

    // ── The 15-second boundary: +/-100 -> +/-200 ─────────────────────────────

    [Fact]
    public void AtEnqueue_TheBandIsNarrow()
    {
        BandAfter(TimeSpan.Zero).Should().Be(MatchmakingBands.NarrowBand);
    }

    [Fact]
    public void JustBefore15Seconds_TheBandIsStillNarrow()
    {
        BandAfter(TimeSpan.FromSeconds(15) - TimeSpan.FromMilliseconds(1))
            .Should().Be(MatchmakingBands.NarrowBand);
    }

    [Fact]
    public void ExactlyAt15Seconds_TheBandHasAlreadyWidenedToMedium()
    {
        // Half-open [15,30): the boundary instant belongs to the NEW band.
        BandAfter(TimeSpan.FromSeconds(15)).Should().Be(MatchmakingBands.MediumBand);
    }

    // ── The 30-second boundary: +/-200 -> +/-400 ─────────────────────────────

    [Fact]
    public void JustBefore30Seconds_TheBandIsStillMedium()
    {
        BandAfter(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1))
            .Should().Be(MatchmakingBands.MediumBand);
    }

    [Fact]
    public void ExactlyAt30Seconds_TheBandHasAlreadyWidenedToWide()
    {
        BandAfter(TimeSpan.FromSeconds(30)).Should().Be(MatchmakingBands.WideBand);
    }

    // ── The 60-second deadline: +/-400 -> expired ────────────────────────────

    [Fact]
    public void JustBefore60Seconds_TheTicketStillHasAWideBand()
    {
        BandAfter(TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(1))
            .Should().Be(MatchmakingBands.WideBand);
    }

    [Fact]
    public void ExactlyAt60Seconds_TheTicketHasNoBandBecauseItHasExpired()
    {
        // Expiry, not a wider band, is the terminal outcome. A ticket at its deadline must not be matchable.
        BandAfter(TimeSpan.FromSeconds(60)).Should().BeNull();
    }

    [Fact]
    public void After60Seconds_TheTicketStillHasNoBand()
    {
        BandAfter(TimeSpan.FromSeconds(90)).Should().BeNull();
    }

    [Fact]
    public void ExactlyAt60Seconds_TheTicketReportsExpired()
    {
        var (ticket, clock) = Enqueued();
        clock.Advance(TimeSpan.FromSeconds(60));

        ticket.IsExpired(clock.GetUtcNow().UtcDateTime).Should().BeTrue();
    }

    [Fact]
    public void JustBefore60Seconds_TheTicketIsNotYetExpired()
    {
        var (ticket, clock) = Enqueued();
        clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(1));

        ticket.IsExpired(clock.GetUtcNow().UtcDateTime).Should().BeFalse();
    }

    // ── Monotonicity: the anti-starvation guarantee ──────────────────────────

    [Fact]
    public void TheBandNeverNarrowsAsATicketAges()
    {
        // This is the property the anti-starvation argument rests on: a waiting ticket's acceptance window only
        // ever grows, so it can never become *harder* to match by waiting longer.
        var (ticket, clock) = Enqueued();
        var previous = 0;

        for (var second = 0; second < 60; second++)
        {
            var band = ticket.CurrentBand(clock.GetUtcNow().UtcDateTime);
            band.Should().NotBeNull($"a ticket {second}s old is still inside its 60s deadline");
            band!.Value.Should().BeGreaterThanOrEqualTo(previous, "the band must never narrow as the ticket ages");

            previous = band.Value;
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    // ── The rating window derived from the band ──────────────────────────────

    [Fact]
    public void TheRatingWindowIsTheRatingPlusOrMinusTheCurrentBand()
    {
        var (ticket, clock) = Enqueued();   // provisional rating 1200

        ticket.RatingWindow(clock.GetUtcNow().UtcDateTime).Should().Be((1100, 1300));

        clock.Advance(TimeSpan.FromSeconds(15));
        ticket.RatingWindow(clock.GetUtcNow().UtcDateTime).Should().Be((1000, 1400));

        clock.Advance(TimeSpan.FromSeconds(15));
        ticket.RatingWindow(clock.GetUtcNow().UtcDateTime).Should().Be((800, 1600));
    }

    [Fact]
    public void AnExpiredTicketHasNoRatingWindow()
    {
        var (ticket, clock) = Enqueued();
        clock.Advance(TimeSpan.FromSeconds(60));

        ticket.RatingWindow(clock.GetUtcNow().UtcDateTime).Should().BeNull();
    }

    [Fact]
    public void ANegativeAgeIsRejectedRatherThanSilentlyBandedAsNarrow()
    {
        // A clock that went backwards is a bug, not a ticket that is "very new". Failing loudly here beats
        // handing the worker a plausible-looking +/-100 band computed from nonsense.
        var act = () => MatchmakingBands.BandFor(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
