using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Lobbies;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Invite lifecycle (<c>Pending -&gt; Accepted|Revoked|Expired</c>) and its 30-minute expiry boundary.
/// An invite is not a membership: it grants the *option* to join, and accepting is what creates the seat.
/// </summary>
public class LobbyInviteTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private readonly Guid _lobby = Guid.NewGuid();
    private readonly Guid _inviter = Guid.NewGuid();
    private readonly Guid _invitee = Guid.NewGuid();

    private LobbyInvite Pending() => LobbyInvite.Create(_lobby, _inviter, _invitee, Now);

    [Fact]
    public void AnInviteExpiresExactlyThirtyMinutesAfterItIsSent()
    {
        var invite = Pending();

        invite.ExpiresAtUtc.Should().Be(LobbyTestFactory.T0.AddMinutes(30));
    }

    [Fact]
    public void JustBeforeThirtyMinutes_TheInviteIsStillAcceptable()
    {
        var invite = Pending();
        _clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromMilliseconds(1));

        invite.CanAccept(Now).Should().BeTrue();
        invite.Accept(Now).Should().Be(LobbyOutcome.Ok);
        invite.State.Should().Be(LobbyInviteState.Accepted);
    }

    [Fact]
    public void ExactlyAtThirtyMinutes_TheInviteCanNoLongerBeAccepted()
    {
        var invite = Pending();
        _clock.Advance(TimeSpan.FromMinutes(30));

        invite.CanAccept(Now).Should().BeFalse();
        invite.Accept(Now).Should().Be(LobbyOutcome.Expired);
        invite.State.Should().Be(LobbyInviteState.Pending, "a failed accept does not itself resolve the invite");
    }

    [Fact]
    public void ARevokedInviteCannotBeAccepted()
    {
        var invite = Pending();
        invite.Revoke(Now).Should().Be(LobbyOutcome.Ok);

        invite.Accept(Now).Should().Be(LobbyOutcome.Closed);
        invite.State.Should().Be(LobbyInviteState.Revoked);
    }

    [Fact]
    public void AnAcceptedInviteCannotBeAcceptedTwice()
    {
        // Otherwise a replayed accept would try to create a second seat for the same user.
        var invite = Pending();
        invite.Accept(Now).Should().Be(LobbyOutcome.Ok);

        invite.Accept(Now).Should().Be(LobbyOutcome.Closed);
    }

    [Fact]
    public void AnAcceptedInviteCannotBeRevoked()
    {
        var invite = Pending();
        invite.Accept(Now);

        invite.Revoke(Now).Should().Be(LobbyOutcome.Closed);
        invite.State.Should().Be(LobbyInviteState.Accepted);
    }

    [Fact]
    public void TheExpirySweepIsIdempotent()
    {
        var invite = Pending();
        _clock.Advance(TimeSpan.FromMinutes(30));

        invite.TryExpire(Now).Should().BeTrue();
        invite.State.Should().Be(LobbyInviteState.Expired);

        invite.TryExpire(Now).Should().BeFalse();
    }

    [Fact]
    public void TheExpirySweepDoesNotResolveAnInviteThatIsNotYetDue()
    {
        var invite = Pending();
        _clock.Advance(TimeSpan.FromMinutes(29));

        invite.TryExpire(Now).Should().BeFalse();
        invite.State.Should().Be(LobbyInviteState.Pending);
    }

    [Fact]
    public void TheExpirySweepDoesNotTouchAnAlreadyAcceptedInvite()
    {
        var invite = Pending();
        invite.Accept(Now);

        _clock.Advance(TimeSpan.FromMinutes(30));

        invite.TryExpire(Now).Should().BeFalse();
        invite.State.Should().Be(LobbyInviteState.Accepted);
    }

    [Fact]
    public void AUserCannotInviteThemselves()
    {
        var act = () => LobbyInvite.Create(_lobby, _inviter, _inviter, Now);

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Start-request bookkeeping. A committed request means the outbox row is durable — never that a match exists
/// (brief Risk #6).
/// </summary>
public class LobbyStartRequestTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private static LobbyStartRequest Open(int revision = 1) =>
        LobbyStartRequest.Open(Guid.NewGuid(), revision, Guid.NewGuid(), "idem-key-1", Guid.NewGuid());

    [Fact]
    public void ANewRequestIsOpenAndCarriesItsRevision()
    {
        var request = Open(revision: 7);

        request.IsOpen.Should().BeTrue();
        request.LobbyRevision.Should().Be(7, "the one-open-request-per-revision index keys on this");
        request.ResolvedAtUtc.Should().BeNull();
    }

    [Fact]
    public void AnOpenRequestCanSucceed()
    {
        var request = Open();

        request.MarkSucceeded(T0).Should().Be(LobbyOutcome.Ok);

        request.State.Should().Be(LobbyStartRequestState.Succeeded);
        request.ResolvedAtUtc.Should().Be(T0);
        request.FailureReason.Should().BeNull();
    }

    [Fact]
    public void AnOpenRequestCanFailWithAReason()
    {
        var request = Open();

        request.MarkFailed("Match runtime unavailable.", T0).Should().Be(LobbyOutcome.Ok);

        request.State.Should().Be(LobbyStartRequestState.Failed);
        request.FailureReason.Should().Be("Match runtime unavailable.");
        request.ResolvedAtUtc.Should().Be(T0);
    }

    [Fact]
    public void AResolvedRequestCannotBeResolvedAgain()
    {
        // M8's responses are delivered at-least-once, so a duplicate MatchCreatedV1 must be a no-op, not a second
        // state change.
        var request = Open();
        request.MarkSucceeded(T0);

        request.MarkSucceeded(T0).Should().Be(LobbyOutcome.Closed);
        request.MarkFailed("late failure", T0).Should().Be(LobbyOutcome.Closed);
        request.State.Should().Be(LobbyStartRequestState.Succeeded, "the first resolution wins");
    }

    [Fact]
    public void ARequestCannotBeOpenedWithoutAnIdempotencyKey()
    {
        var act = () => LobbyStartRequest.Open(Guid.NewGuid(), 1, Guid.NewGuid(), "", Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ARequestCannotBeOpenedWithoutAMatchRequestId()
    {
        var act = () => LobbyStartRequest.Open(Guid.NewGuid(), 1, Guid.Empty, "idem", Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }
}
