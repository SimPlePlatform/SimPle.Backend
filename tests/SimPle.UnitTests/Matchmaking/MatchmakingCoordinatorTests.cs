using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.Outbox;
using SimPle.Application.Matchmaking.Services;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// One matching cycle: the Module 8 gate, the atomic assignment+event commit, and the one-event-per-group rule.
///
/// The claim <em>race</em> is not asserted here and cannot be — <c>FOR UPDATE SKIP LOCKED</c> and the partial unique
/// index on active assignment are database behavior, and a substituted repository would prove only that the
/// substitute works. Those are proven in <c>MatchmakingPostgresConcurrencyTests</c> against real PostgreSQL.
/// </summary>
public sealed class MatchmakingCoordinatorTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly IMatchmakingRepository _tickets = Substitute.For<IMatchmakingRepository>();
    private readonly IMatchRuntimeProbe _matchRuntime = Substitute.For<IMatchRuntimeProbe>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly MatchmakingCoordinator _sut;

    public MatchmakingCoordinatorTests()
    {
        _tickets.GetBlockedPairsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Guid, Guid)>());
        _tickets.GetOldestQueuedAgeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((TimeSpan?)null);
        _tickets.ClaimQueuedTicketsAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MatchmakingTicket>());

        _matchRuntime.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        // The honest default: no match runtime, because Module 8 does not exist.
        _matchRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        _sut = new MatchmakingCoordinator(
            _tickets, new PassThroughWorkerTransaction(), _matchRuntime,
            Options.Create(new MatchmakingOptions()),
            _clock,
            NullLogger<MatchmakingCoordinator>.Instance);
    }

    // ── The Module 8 gate ────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoMatchRuntime_TheCycleDoesNotRunAtAll_AndClaimsNothing()
    {
        // The single most important assertion in this slice. A cycle that ran without M8 would mark tickets Matched
        // and emit match requests nobody consumes — the queue UI would show a found opponent and a room the player
        // could never enter. That is the fabrication the brief forbids (Risk #6). Before M8 the queue honestly does
        // nothing but widen and time out.
        Given(TicketFactory.Queued(T0), TicketFactory.Queued(T0));

        var result = await _sut.RunCycleAsync("worker-1");

        result.Executed.Should().BeFalse();
        result.TicketsMatched.Should().Be(0);

        await _tickets.DidNotReceive().ClaimQueuedTicketsAsync(
            Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _tickets.DidNotReceive().AddAssignmentsAsync(
            Arg.Any<IReadOnlyList<MatchmakingAssignment>>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoMatchRuntime_QueueAgeIsStillReported_SoAStalledQueueIsVisible()
    {
        _tickets.GetOldestQueuedAgeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromSeconds(42));

        var result = await _sut.RunCycleAsync("worker-1");

        result.Executed.Should().BeFalse();
        result.OldestQueuedAge.Should().Be(TimeSpan.FromSeconds(42));
    }

    // ── A live cycle ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithAMatchRuntime_ACompatiblePairIsMatchedAndCommittedAtomically()
    {
        WithMatchRuntime();
        var a = TicketFactory.Queued(T0);
        var b = TicketFactory.Queued(T0);
        Given(a, b);

        IReadOnlyList<MatchmakingAssignment>? assignments = null;
        IReadOnlyList<OutboxMessage>? events = null;
        await _tickets.AddAssignmentsAsync(
            Arg.Do<IReadOnlyList<MatchmakingAssignment>>(x => assignments = x),
            Arg.Do<IReadOnlyList<OutboxMessage>>(x => events = x),
            Arg.Any<CancellationToken>());

        var result = await _sut.RunCycleAsync("worker-1");

        result.Executed.Should().BeTrue();
        result.ProposalsFormed.Should().Be(1);
        result.TicketsMatched.Should().Be(2);

        a.State.Should().Be(MatchmakingTicketState.Matched);
        b.State.Should().Be(MatchmakingTicketState.Matched);

        // The worker id survives onto the terminal rows — that attribution is what backs the
        // matchmaking-worker-failure signal.
        a.ClaimedByWorker.Should().Be("worker-1");
        b.ClaimedByWorker.Should().Be("worker-1");

        assignments.Should().HaveCount(2);
        assignments!.Select(x => x.GroupId).Distinct().Should().ContainSingle("both tickets share one group");
        assignments.Select(x => x.MatchRequestId).Distinct().Should().ContainSingle("one group, one match request");
        assignments.Select(x => x.TicketId).Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task AGroupEmitsExactlyOneMatchRequestedEvent_NotOnePerTicket()
    {
        // The group is what M8 creates a match from. One event per ticket would either make M8 build two matches for
        // one proposal, or force it to de-duplicate them itself.
        WithMatchRuntime();
        Given(TicketFactory.Queued(T0), TicketFactory.Queued(T0));

        IReadOnlyList<OutboxMessage>? events = null;
        await _tickets.AddAssignmentsAsync(
            Arg.Any<IReadOnlyList<MatchmakingAssignment>>(),
            Arg.Do<IReadOnlyList<OutboxMessage>>(x => events = x),
            Arg.Any<CancellationToken>());

        await _sut.RunCycleAsync("worker-1");

        events.Should().ContainSingle();
        events![0].EventType.Should().Be(MatchmakingOutbox.MatchRequested);

        // Ids only — no rating, no region, no profile snapshot.
        events[0].Payload.Should().NotContain("1200");
        events[0].Payload.Should().Contain("matchmaking");
    }

    [Fact]
    public async Task ATicketWhoseOwnerEnteredALiveMatchIsSkipped_AndLeftQueued()
    {
        // The cross-cutting active-participation rule, re-asked at assignment. A player can enter a live match in
        // the sixty seconds their ticket is queued; a single up-front check at enqueue would happily assign them a
        // second one.
        WithMatchRuntime();

        var busy = TicketFactory.Queued(T0);
        var free = TicketFactory.Queued(T0);
        Given(busy, free);

        _matchRuntime.IsInActiveMatchAsync(busy.UserId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.RunCycleAsync("worker-1");

        result.TicketsMatched.Should().Be(0);
        busy.State.Should().Be(MatchmakingTicketState.Queued);   // still in the queue, not punished
        free.State.Should().Be(MatchmakingTicketState.Queued);

        await _tickets.DidNotReceive().AddAssignmentsAsync(
            Arg.Any<IReadOnlyList<MatchmakingAssignment>>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedTicketOwnersAreNeverAssignedToEachOther()
    {
        WithMatchRuntime();

        var a = TicketFactory.Queued(T0);
        var b = TicketFactory.Queued(T0);
        Given(a, b);

        _tickets.GetBlockedPairsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { (a.UserId, b.UserId) });

        var result = await _sut.RunCycleAsync("worker-1");

        result.TicketsMatched.Should().Be(0);
        a.State.Should().Be(MatchmakingTicketState.Queued);
        b.State.Should().Be(MatchmakingTicketState.Queued);
    }

    [Fact]
    public async Task AnEmptyQueueCommitsNothing()
    {
        WithMatchRuntime();

        var result = await _sut.RunCycleAsync("worker-1");

        result.Executed.Should().BeTrue();
        result.TicketsClaimed.Should().Be(0);

        await _tickets.DidNotReceive().AddAssignmentsAsync(
            Arg.Any<IReadOnlyList<MatchmakingAssignment>>(), Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnmatchableLoneTicketCommitsNothingAndStaysQueued()
    {
        WithMatchRuntime();
        var lonely = TicketFactory.Queued(T0);
        Given(lonely);

        var result = await _sut.RunCycleAsync("worker-1");

        result.TicketsClaimed.Should().Be(1);
        result.TicketsMatched.Should().Be(0);
        lonely.State.Should().Be(MatchmakingTicketState.Queued);
    }

    [Fact]
    public async Task AnEmptyWorkerIdIsRejected_BecauseAnUnattributedClaimIsUntraceable()
    {
        var act = async () => await _sut.RunCycleAsync("  ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WithMatchRuntime() =>
        _matchRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);

    private void Given(params MatchmakingTicket[] tickets) =>
        _tickets.ClaimQueuedTicketsAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(tickets);
}
