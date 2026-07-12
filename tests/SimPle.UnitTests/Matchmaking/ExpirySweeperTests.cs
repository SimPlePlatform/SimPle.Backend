using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Expiry;
using SimPle.Application.Lobbies.Outbox;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The expiry sweep: 60-second tickets, 2-hour lobbies, 30-minute invites.
///
/// Every deadline here is only provable with an injected clock (brief Risk #8) — a wall-clock test could not tell a
/// correct 2-hour lobby lifetime from one that never expires at all without running for two hours.
/// </summary>
public sealed class ExpirySweeperTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly IMatchmakingRepository _tickets = Substitute.For<IMatchmakingRepository>();
    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly ExpirySweeper _sut;

    public ExpirySweeperTests()
    {
        _tickets.GetExpiredTicketsAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MatchmakingTicket>());
        _lobbies.GetExpiredLobbiesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Lobby>());
        _lobbies.GetExpiredInvitesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LobbyInvite>());

        _sut = new ExpirySweeper(
            _tickets, _lobbies, new PassThroughWorkerTransaction(),
            Options.Create(new ExpiryOptions()),
            _clock,
            NullLogger<ExpirySweeper>.Instance);
    }

    // ── Tickets ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATicketPastItsSixtySecondDeadlineIsTimedOut()
    {
        var ticket = TicketFactory.Queued(T0);
        GivenTickets(ticket);

        _clock.SetUtcNow(T0.AddSeconds(60));   // exactly at the deadline is already expired
        var result = await _sut.SweepAsync();

        result.TicketsExpired.Should().Be(1);
        ticket.State.Should().Be(MatchmakingTicketState.TimedOut);
    }

    [Fact]
    public async Task TheSweepRunsWithoutModule8_SoAQueuedTicketAlwaysReachesAnHonestOutcome()
    {
        // The asymmetry with the matching worker, and the reason for it: matching without a match runtime would
        // fabricate an opponent, but expiring without one fabricates nothing. Without this, a Phase-1 ticket would
        // sit Queued forever, because the only thing that could ever have resolved it does not exist yet.
        //
        // The sweeper takes no IMatchRuntimeProbe at all — it *cannot* be gated on M8, by construction.
        var ticket = TicketFactory.Queued(T0);
        GivenTickets(ticket);

        _clock.SetUtcNow(T0.AddSeconds(61));
        var result = await _sut.SweepAsync();

        result.TicketsExpired.Should().Be(1);
        ticket.State.Should().Be(MatchmakingTicketState.TimedOut);
    }

    [Fact]
    public async Task TheSweepReportsHowLateItWas_WhichIsTheExpiryLagSignal()
    {
        var ticket = TicketFactory.Queued(T0);
        GivenTickets(ticket);

        _clock.SetUtcNow(T0.AddSeconds(63));   // 3 seconds past the 60-second deadline
        var result = await _sut.SweepAsync();

        result.MaxTicketLag.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ASecondSweepOverTheSameTicketIsANoOp()
    {
        // Idempotent by construction: TryTimeOut returns false rather than transitioning twice, so an overlapping
        // or re-run sweep cannot double-expire anything.
        var ticket = TicketFactory.Queued(T0);
        GivenTickets(ticket);

        _clock.SetUtcNow(T0.AddSeconds(61));
        (await _sut.SweepAsync()).TicketsExpired.Should().Be(1);
        (await _sut.SweepAsync()).TicketsExpired.Should().Be(0);

        ticket.State.Should().Be(MatchmakingTicketState.TimedOut);
    }

    // ── Lobbies ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALobbyPastItsTwoHourLifetimeIsExpired_AndEmitsAClosedEvent()
    {
        var lobby = LobbyTestFactory.Open(Guid.NewGuid(), T0);
        GivenLobbies(lobby);

        _clock.SetUtcNow(T0.AddHours(2));
        var result = await _sut.SweepAsync();

        result.LobbiesExpired.Should().Be(1);
        lobby.State.Should().Be(LobbyState.Expired);
        lobby.ClosedReason.Should().Be(LobbyClosedReason.Expired);

        // A consumer must never have to infer *why* a lobby ended from the absence of anything else.
        await _tickets.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<OutboxMessage>>(e =>
                e.Count == 1 && e[0].EventType == LobbyOutbox.LobbyClosed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALobbyJustShortOfTwoHoursSurvives()
    {
        var lobby = LobbyTestFactory.Open(Guid.NewGuid(), T0);

        // The repository query is what filters on the deadline; feed it the truth so this test exercises the
        // boundary rather than the substitute's willingness to return whatever it is handed.
        _lobbies.GetExpiredLobbiesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => lobby.IsExpired(call.Arg<DateTime>())
                ? new[] { lobby }
                : Array.Empty<Lobby>());

        _clock.SetUtcNow(T0.AddHours(2).AddSeconds(-1));
        var result = await _sut.SweepAsync();

        result.LobbiesExpired.Should().Be(0);
        lobby.State.Should().Be(LobbyState.Open);
    }

    // ── Invites ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInvitePastItsThirtyMinuteLifetimeIsExpired_AndEmitsNoEvent()
    {
        // Nobody acted. M11 has no notification to send for "an invite you ignored has quietly lapsed", so the
        // state change alone is the record.
        var invite = LobbyInvite.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);
        GivenInvites(invite);

        _clock.SetUtcNow(T0.AddMinutes(30));
        var result = await _sut.SweepAsync();

        result.InvitesExpired.Should().Be(1);
        invite.State.Should().Be(LobbyInviteState.Expired);

        await _tickets.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<OutboxMessage>>(e => e.Count == 0), Arg.Any<CancellationToken>());
    }

    // ── Nothing to do ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIdleSweepWritesNothingAtAll()
    {
        var result = await _sut.SweepAsync();

        result.Total.Should().Be(0);
        await _tickets.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void GivenTickets(params MatchmakingTicket[] tickets) =>
        _tickets.GetExpiredTicketsAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(tickets);

    private void GivenLobbies(params Lobby[] lobbies) =>
        _lobbies.GetExpiredLobbiesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(lobbies);

    private void GivenInvites(params LobbyInvite[] invites) =>
        _lobbies.GetExpiredInvitesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(invites);
}
