using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Matchmaking;
using SimPle.UnitTests.Lobbies;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// Ticket lifecycle: <c>Queued -&gt; Claimed -&gt; Matched|Requeued|Failed</c>, or
/// <c>Queued -&gt; Cancelled|TimedOut</c>, plus the honest provisional rating snapshot.
/// </summary>
public class MatchmakingTicketTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Rating provenance ────────────────────────────────────────────────────

    [Fact]
    public void EveryTicketRecordsTheProvisionalRatingSource()
    {
        // M10 does not exist. The legacy global User.Elo is deliberately NOT substituted: it is one cross-game
        // number, so presenting it as a per-game rating would be a fabricated signal.
        var ticket = TicketFactory.Queued(Now);

        ticket.Rating.Should().Be(1200);
        ticket.RatingSourceVersion.Should().Be("provisional-1200-v1");
    }

    [Fact]
    public void ATicketSnapshotsItsCandidatePoolKeyAtEnqueue()
    {
        var ticket = TicketFactory.Queued(Now, gameSlug: "checkers", mode: "ranked", timeControlId: "rapid-10-0",
            rated: true, resolvedRegion: "us-east");

        ticket.GameSlug.Should().Be("checkers");
        ticket.CapabilityVersion.Should().Be(1);
        ticket.Mode.Should().Be("ranked");
        ticket.TimeControlId.Should().Be("rapid-10-0");
        ticket.Rated.Should().BeTrue();
        ticket.ResolvedRegion.Should().Be("us-east");
        ticket.EnqueuedAtUtc.Should().Be(LobbyTestFactory.T0);
        ticket.DeadlineAtUtc.Should().Be(LobbyTestFactory.T0.AddSeconds(60));
    }

    [Fact]
    public void ATicketCannotBeEnqueuedWithAnUnresolvedRegion()
    {
        var act = () => TicketFactory.Queued(Now, resolvedRegion: "Auto");

        act.Should().Throw<ArgumentException>().WithMessage("*Auto*");
    }

    // ── Claim ────────────────────────────────────────────────────────────────

    [Fact]
    public void AQueuedTicketCanBeClaimed()
    {
        var ticket = TicketFactory.Queued(Now);

        ticket.Claim("worker-1", Now).Should().Be(MatchmakingOutcome.Ok);

        ticket.State.Should().Be(MatchmakingTicketState.Claimed);
        ticket.ClaimedByWorker.Should().Be("worker-1");
        ticket.ClaimedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void AnAlreadyClaimedTicketCannotBeClaimedAgain()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);

        ticket.Claim("worker-2", Now).Should().Be(MatchmakingOutcome.AlreadyClaimed);

        ticket.ClaimedByWorker.Should().Be("worker-1", "the first claim holds");
    }

    [Fact]
    public void AnExpiredTicketCannotBeClaimed()
    {
        // The expiry sweep owns it now; claiming it would hand a worker a ticket that has already run out the clock.
        var ticket = TicketFactory.Queued(Now);
        _clock.Advance(TimeSpan.FromSeconds(60));

        ticket.Claim("worker-1", Now).Should().Be(MatchmakingOutcome.Expired);
        ticket.State.Should().Be(MatchmakingTicketState.Queued);
    }

    // ── Matched ──────────────────────────────────────────────────────────────

    [Fact]
    public void AClaimedTicketCanBeMatched()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);

        ticket.MarkMatched(Now).Should().Be(MatchmakingOutcome.Ok);

        ticket.State.Should().Be(MatchmakingTicketState.Matched);
        ticket.ResolvedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void AMatchedTicketKeepsItsWorkerAttribution()
    {
        // The database CHECK only forbids a worker id on a *Queued* row. Terminal rows keep it, because it is the
        // attribution behind the matchmaking-worker-failure signal.
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);
        ticket.MarkMatched(Now);

        ticket.ClaimedByWorker.Should().Be("worker-1");
    }

    [Fact]
    public void AQueuedTicketCannotJumpStraightToMatched()
    {
        var ticket = TicketFactory.Queued(Now);

        ticket.MarkMatched(Now).Should().Be(MatchmakingOutcome.InvalidTransition);
    }

    // ── Requeue ──────────────────────────────────────────────────────────────

    [Fact]
    public void ARequeueReturnsTheTicketToTheQueueAndClearsTheWorker()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);

        _clock.Advance(TimeSpan.FromSeconds(5));
        ticket.Requeue(Now).Should().Be(MatchmakingOutcome.Ok);

        ticket.State.Should().Be(MatchmakingTicketState.Queued);
        ticket.ClaimedByWorker.Should().BeNull("a queued ticket naming a worker would mean a claim leaked");
        ticket.ClaimedAtUtc.Should().BeNull();
        ticket.RetryBudget.Should().Be(MatchmakingTicket.DefaultRetryBudget - 1);
    }

    [Fact]
    public void ARequeueNeverExtendsTheOriginalDeadline()
    {
        // "A failed M8 handoff requeues before the original deadline" — a retry must not buy the ticket more time,
        // or a persistently failing handoff could keep one ticket alive indefinitely.
        var ticket = TicketFactory.Queued(Now);
        var originalDeadline = ticket.DeadlineAtUtc;

        ticket.Claim("worker-1", Now);
        _clock.Advance(TimeSpan.FromSeconds(10));
        ticket.Requeue(Now);

        ticket.DeadlineAtUtc.Should().Be(originalDeadline);
    }

    [Fact]
    public void WhenTheRetryBudgetRunsOut_TheTicketFails()
    {
        var ticket = TicketFactory.Queued(Now);

        for (var attempt = 0; attempt < MatchmakingTicket.DefaultRetryBudget; attempt++)
        {
            ticket.Claim($"worker-{attempt}", Now).Should().Be(MatchmakingOutcome.Ok);
            ticket.Requeue(Now).Should().Be(MatchmakingOutcome.Ok);
        }

        ticket.RetryBudget.Should().Be(0);

        ticket.Claim("worker-final", Now).Should().Be(MatchmakingOutcome.Ok);
        ticket.Requeue(Now).Should().Be(MatchmakingOutcome.RetryBudgetExhausted);

        ticket.State.Should().Be(MatchmakingTicketState.Failed);
    }

    [Fact]
    public void ARequeuePastTheDeadlineTimesOutInsteadOfReturningToTheQueue()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);

        _clock.Advance(TimeSpan.FromSeconds(60));

        ticket.Requeue(Now).Should().Be(MatchmakingOutcome.Expired);
        ticket.State.Should().Be(MatchmakingTicketState.TimedOut, "there is nothing left to retry into");
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public void AQueuedTicketCanBeCancelled()
    {
        var ticket = TicketFactory.Queued(Now);

        ticket.Cancel(Now).Should().Be(MatchmakingOutcome.Ok);

        ticket.State.Should().Be(MatchmakingTicketState.Cancelled);
    }

    [Fact]
    public void ACancelAfterAClaimIsTooLateButIsNotAnError()
    {
        // Per the error catalogue, Matchmaking.CancelTooLate is a 200: the caller returns the ticket's current
        // status rather than failing. The user is already being matched; there is nothing honest to cancel.
        var ticket = TicketFactory.Queued(Now);
        ticket.Claim("worker-1", Now);

        ticket.Cancel(Now).Should().Be(MatchmakingOutcome.AlreadyClaimed);

        ticket.State.Should().Be(MatchmakingTicketState.Claimed, "the cancel did not take effect");
    }

    [Fact]
    public void CancellingATerminalTicketIsRejected()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Cancel(Now);

        ticket.Cancel(Now).Should().Be(MatchmakingOutcome.Terminal);
    }

    // ── Expiry sweep ─────────────────────────────────────────────────────────

    [Fact]
    public void TheExpirySweepTimesOutADueTicket()
    {
        var ticket = TicketFactory.Queued(Now);
        _clock.Advance(TimeSpan.FromSeconds(60));

        ticket.TryTimeOut(Now).Should().BeTrue();

        ticket.State.Should().Be(MatchmakingTicketState.TimedOut);
        ticket.ResolvedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void TheExpirySweepIsIdempotent()
    {
        var ticket = TicketFactory.Queued(Now);
        _clock.Advance(TimeSpan.FromSeconds(60));
        ticket.TryTimeOut(Now).Should().BeTrue();

        ticket.TryTimeOut(Now).Should().BeFalse("a re-run over an already-terminal ticket is a no-op");
    }

    [Fact]
    public void TheExpirySweepDoesNotTouchATicketThatIsNotYetDue()
    {
        var ticket = TicketFactory.Queued(Now);
        _clock.Advance(TimeSpan.FromSeconds(59));

        ticket.TryTimeOut(Now).Should().BeFalse();
        ticket.State.Should().Be(MatchmakingTicketState.Queued);
    }

    [Fact]
    public void TheExpirySweepDoesNotResurrectACancelledTicket()
    {
        var ticket = TicketFactory.Queued(Now);
        ticket.Cancel(Now);

        _clock.Advance(TimeSpan.FromSeconds(60));

        ticket.TryTimeOut(Now).Should().BeFalse();
        ticket.State.Should().Be(MatchmakingTicketState.Cancelled, "a cancelled ticket must not become TimedOut");
    }
}

public class MatchmakingAssignmentTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    [Fact]
    public void AnAssignmentStartsActive()
    {
        var assignment = MatchmakingAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);

        assignment.IsActive.Should().BeTrue();
        assignment.CreatedAtUtc.Should().Be(T0);
    }

    [Fact]
    public void SupersedingReleasesTheActiveSlotSoTheTicketCanBeAssignedAgain()
    {
        // The partial unique index only counts Active rows, so superseding is what makes a legitimate re-assignment
        // possible without ever permitting two live assignments for one ticket.
        var assignment = MatchmakingAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);

        assignment.Supersede(T0).Should().Be(MatchmakingOutcome.Ok);

        assignment.State.Should().Be(MatchmakingAssignmentState.Superseded);
        assignment.IsActive.Should().BeFalse();
        assignment.ResolvedAtUtc.Should().Be(T0);
    }

    [Fact]
    public void ATerminalAssignmentCannotBeResolvedTwice()
    {
        var assignment = MatchmakingAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);
        assignment.Supersede(T0);

        assignment.Supersede(T0).Should().Be(MatchmakingOutcome.InvalidTransition);
        assignment.MarkFailed(T0).Should().Be(MatchmakingOutcome.InvalidTransition);
        assignment.State.Should().Be(MatchmakingAssignmentState.Superseded);
    }

    [Fact]
    public void EveryTicketInOneProposalSharesAGroupId()
    {
        var groupId = Guid.NewGuid();
        var matchRequestId = Guid.NewGuid();

        var a = MatchmakingAssignment.Create(Guid.NewGuid(), matchRequestId, groupId, T0);
        var b = MatchmakingAssignment.Create(Guid.NewGuid(), matchRequestId, groupId, T0);

        a.GroupId.Should().Be(b.GroupId);
        a.MatchRequestId.Should().Be(b.MatchRequestId, "one proposal hands off as one match request");
    }
}
