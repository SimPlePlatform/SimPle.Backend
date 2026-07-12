using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Lobbies;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Deterministic host transfer (brief Risk #3): "transfer to the longest-tenured eligible joined human, tie-broken
/// by user id; the lobby closes only when none remains."
///
/// This is fixed policy, not an open question — an ambiguous ordering would let two clients compute different hosts
/// from the same roster and disagree about who may start the match. The tie-break test is the important one: it is
/// the only thing standing between "deterministic" and "whatever order the list happened to be in".
/// </summary>
public class LobbyHostTransferTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private readonly Guid _host = Guid.NewGuid();

    [Fact]
    public void WhenTheHostLeaves_TheLongestTenuredMemberBecomesHost()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        var early = Guid.NewGuid();
        lobby.Join(early, Now);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var late = Guid.NewGuid();
        lobby.Join(late, Now);

        var result = lobby.Leave(_host, Now);

        result.Outcome.Should().Be(LobbyOutcome.Ok);
        result.NewHostUserId.Should().Be(early, "tenure is measured by JoinedAtUtc, and 'early' joined 5 minutes sooner");
        lobby.HostUserId.Should().Be(early);
        result.ClosedReason.Should().BeNull();
    }

    [Fact]
    public void WhenTwoMembersShareTheSameJoinInstant_TheLowerUserIdWinsTheTieBreak()
    {
        // Two joins inside the same clock tick is not exotic — it is what a batch invite-accept looks like. Without
        // the user-id tie-break the winner would depend on list order, and two clients could disagree.
        var lobby = LobbyTestFactory.Open(_host, Now);

        var lower = new Guid("00000000-0000-0000-0000-00000000000a");
        var higher = new Guid("00000000-0000-0000-0000-00000000000b");

        // Join the HIGHER id first, so insertion order and the correct answer disagree.
        lobby.Join(higher, Now);
        lobby.Join(lower, Now);

        var result = lobby.Leave(_host, Now);

        result.NewHostUserId.Should().Be(lower, "identical tenure is tie-broken by the lower user id, not by join order");
        lobby.HostUserId.Should().Be(lower);
    }

    [Fact]
    public void TheNewHostBecomesImplicitlyReady()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        var successor = Guid.NewGuid();
        lobby.Join(successor, Now);

        lobby.FindJoinedMember(successor)!.IsReady.Should().BeFalse("a joiner starts un-ready");

        lobby.Leave(_host, Now);

        lobby.FindJoinedMember(successor)!.IsReady.Should()
            .BeTrue("the host is implicitly ready, and that must hold for a host who arrived by transfer");
    }

    [Fact]
    public void AMemberWhoAlreadyLeftIsNotEligibleToInheritTheHost()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        var departed = Guid.NewGuid();
        lobby.Join(departed, Now);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var stayed = Guid.NewGuid();
        lobby.Join(stayed, Now);

        lobby.Leave(departed, Now);   // the longest-tenured member leaves first

        var result = lobby.Leave(_host, Now);

        result.NewHostUserId.Should().Be(stayed, "only a *joined* member is eligible");
    }

    [Fact]
    public void AKickedMemberIsNotEligibleToInheritTheHost()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        var kicked = Guid.NewGuid();
        lobby.Join(kicked, Now);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var stayed = Guid.NewGuid();
        lobby.Join(stayed, Now);

        lobby.Kick(_host, kicked, Now);

        var result = lobby.Leave(_host, Now);

        result.NewHostUserId.Should().Be(stayed);
    }

    [Fact]
    public void WhenTheLastMemberLeaves_TheLobbyClosesWithAnAuditableReason()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        var result = lobby.Leave(_host, Now);

        result.Outcome.Should().Be(LobbyOutcome.Ok);
        result.NewHostUserId.Should().BeNull();
        result.ClosedReason.Should().Be(LobbyClosedReason.NoEligibleHost);
        lobby.State.Should().Be(LobbyState.Closed);
        lobby.ClosedReason.Should().Be(LobbyClosedReason.NoEligibleHost);
    }

    [Fact]
    public void ANonHostLeaveDoesNotTransferTheHost()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        var member = Guid.NewGuid();
        lobby.Join(member, Now);

        var result = lobby.Leave(member, Now);

        result.NewHostUserId.Should().BeNull();
        lobby.HostUserId.Should().Be(_host);
        lobby.State.Should().Be(LobbyState.Open);
    }

    [Fact]
    public void TheHostCannotKickThemselves()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(Guid.NewGuid(), Now);

        var outcome = lobby.Kick(_host, _host, Now);

        outcome.Should().Be(LobbyOutcome.InvalidTarget, "the host leaves; they do not kick themselves");
        lobby.FindJoinedMember(_host).Should().NotBeNull();
    }

    [Fact]
    public void ANonHostCannotKick()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        lobby.Join(alice, Now);
        lobby.Join(bob, Now);

        var outcome = lobby.Kick(alice, bob, Now);

        outcome.Should().Be(LobbyOutcome.Forbidden);
        lobby.FindJoinedMember(bob).Should().NotBeNull();
    }

    [Fact]
    public void ANonMemberGetsTheePrivacySafeNotMemberOutcome_NotForbidden()
    {
        // A stranger must not be able to tell a host-only action apart from a lobby that does not exist: Forbidden
        // would confirm the lobby id is real. The service maps NotMember to the privacy-safe not-found.
        var lobby = LobbyTestFactory.Open(_host, Now);
        var stranger = Guid.NewGuid();

        lobby.Kick(stranger, _host, Now).Should().Be(LobbyOutcome.NotMember);
        lobby.ChangeSettings(stranger, LobbyTestFactory.Settings(), Now).Should().Be(LobbyOutcome.NotMember);
        lobby.BeginStarting(stranger, Now).Should().Be(LobbyOutcome.NotMember);
    }
}
