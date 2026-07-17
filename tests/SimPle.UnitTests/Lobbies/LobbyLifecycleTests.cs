using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Lobbies;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Lobby lifecycle, capacity, and the 2-hour expiry — the expiry boundary probed at, just below, and just above,
/// which only an injected clock can do (brief Risk #8).
/// </summary>
public class LobbyLifecycleTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();

    // ── The 2-hour expiry boundary ───────────────────────────────────────────

    [Fact]
    public void AnOpenLobbyExpiresExactlyTwoHoursAfterCreation()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        lobby.ExpiresAtUtc.Should().Be(LobbyTestFactory.T0.AddHours(2));
    }

    [Fact]
    public void JustBeforeTwoHours_TheLobbyIsStillJoinable()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        _clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMilliseconds(1));

        lobby.IsExpired(Now).Should().BeFalse();
        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.Ok);
    }

    [Fact]
    public void ExactlyAtTwoHours_TheLobbyIsExpiredAndRejectsMutations()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        _clock.Advance(TimeSpan.FromHours(2));

        lobby.IsExpired(Now).Should().BeTrue();
        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.Expired);
        lobby.SetReadiness(_host, true, Now).Should().Be(LobbyOutcome.Expired);
        lobby.ChangeSettings(_host, LobbyTestFactory.Settings(), Now).Should().Be(LobbyOutcome.Expired);
        lobby.BeginStarting(_host, Now).Should().Be(LobbyOutcome.Expired);
    }

    [Fact]
    public void TheExpirySweepIsIdempotent()
    {
        // The worker will re-run this over the same rows; a second call must be a no-op, not a second state change.
        var lobby = LobbyTestFactory.Open(_host, Now);
        _clock.Advance(TimeSpan.FromHours(2));

        lobby.TryExpire(Now).Should().BeTrue();
        lobby.State.Should().Be(LobbyState.Expired);
        lobby.ClosedReason.Should().Be(LobbyClosedReason.Expired);
        var revisionAfterFirst = lobby.Revision;

        lobby.TryExpire(Now).Should().BeFalse("the lobby is already expired");
        lobby.Revision.Should().Be(revisionAfterFirst);
    }

    [Fact]
    public void TheExpirySweepDoesNotTouchALobbyThatIsNotYetDue()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        _clock.Advance(TimeSpan.FromHours(1));

        lobby.TryExpire(Now).Should().BeFalse();
        lobby.State.Should().Be(LobbyState.Open);
    }

    /// <summary>
    /// Regression: expiry used to only flip Lobby.State, leaving every still-seated member's own
    /// LobbyMemberState stuck at Joined. That row survives forever against the DB's partial unique index on
    /// (UserId, Joined) — even though the lobby itself is terminal and invisible to GetActiveLobbyForUserAsync —
    /// permanently blocking that user from ever joining or creating another lobby.
    /// </summary>
    [Fact]
    public void ExpiryReleasesEveryRemainingSeatedMember()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.Ok);
        _clock.Advance(TimeSpan.FromHours(2));

        lobby.TryExpire(Now).Should().BeTrue();

        lobby.JoinedCount.Should().Be(0);
        lobby.FindJoinedMember(_host).Should().BeNull();
        lobby.FindJoinedMember(_alice).Should().BeNull();
    }

    /// <summary>Same guarantee for an explicit Close, not just expiry.</summary>
    [Fact]
    public void ClosingALobbyReleasesEveryRemainingSeatedMember()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.Ok);

        lobby.Close(LobbyClosedReason.HostClosed, Now).Should().Be(LobbyOutcome.Ok);

        lobby.JoinedCount.Should().Be(0);
        lobby.FindJoinedMember(_host).Should().BeNull();
        lobby.FindJoinedMember(_alice).Should().BeNull();
    }

    // ── Capacity ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheHostOccupiesASeatFromCreation()
    {
        var lobby = LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(maxPlayers: 2));

        lobby.JoinedCount.Should().Be(1);
    }

    [Fact]
    public void AJoinIntoAFullLobbyIsRejected()
    {
        var lobby = LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(maxPlayers: 2));
        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.Ok);   // lobby is now 2/2

        lobby.Join(Guid.NewGuid(), Now).Should().Be(LobbyOutcome.Full);
        lobby.JoinedCount.Should().Be(2);
    }

    [Fact]
    public void ASeatFreedByALeaveCanBeTakenAgain()
    {
        var lobby = LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(maxPlayers: 2));
        lobby.Join(_alice, Now);

        lobby.Leave(_alice, Now);

        lobby.Join(Guid.NewGuid(), Now).Should().Be(LobbyOutcome.Ok);
    }

    [Fact]
    public void JoiningTwiceIsRejected()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(_alice, Now);

        lobby.Join(_alice, Now).Should().Be(LobbyOutcome.AlreadyJoined);
        lobby.JoinedCount.Should().Be(2);
    }

    [Fact]
    public void SettingsCannotShrinkMaxPlayersBelowTheSeatedRoster()
    {
        // Shrinking under the roster would strand seated members with no defined eviction rule.
        var lobby = LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(maxPlayers: 4));
        lobby.Join(_alice, Now);
        lobby.Join(Guid.NewGuid(), Now);   // 3 seated

        var outcome = lobby.ChangeSettings(_host, LobbyTestFactory.Settings(maxPlayers: 2), Now);

        outcome.Should().Be(LobbyOutcome.Full);
        lobby.MaxPlayers.Should().Be(4);
    }

    // ── Start preconditions ──────────────────────────────────────────────────

    [Fact]
    public void AStartRequiresEveryoneReady()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(_alice, Now);   // alice is not ready

        lobby.CanStart(Now).Should().BeFalse();
        lobby.BeginStarting(_host, Now).Should().Be(LobbyOutcome.NotStartable);
    }

    [Fact]
    public void AStartRequiresAtLeastTwoPlayers()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);   // host alone, implicitly ready

        lobby.IsEveryoneReady.Should().BeTrue("the host is the only member and is implicitly ready");
        lobby.CanStart(Now).Should().BeFalse("a solo lobby is not a match");
    }

    [Fact]
    public void AReadyLobbyCanStart()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.CanStart(Now).Should().BeTrue();
        lobby.BeginStarting(_host, Now).Should().Be(LobbyOutcome.Ok);
        lobby.State.Should().Be(LobbyState.Starting);
    }

    [Fact]
    public void ARatedLobbyCannotStartWhileAiFillIsRequested()
    {
        // M9 does not exist, so a "rated" match with an unfillable AI seat would either hang or silently become
        // unranked. Refusing to start is the honest option.
        var settings = LobbyTestFactory.Settings(rated: true, aiFillRequested: true);
        var lobby = LobbyTestFactory.Open(_host, Now, settings);
        lobby.Join(_alice, Now);
        lobby.SetReadiness(_alice, true, Now);

        lobby.IsEveryoneReady.Should().BeTrue();
        lobby.CanStart(Now).Should().BeFalse();
        lobby.BeginStarting(_host, Now).Should().Be(LobbyOutcome.NotStartable);
    }

    [Fact]
    public void OnlyTheHostMayStart()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.BeginStarting(_alice, Now).Should().Be(LobbyOutcome.Forbidden);
        lobby.State.Should().Be(LobbyState.Open);
    }

    // ── Starting -> Started / back to Open ───────────────────────────────────

    [Fact]
    public void OnlyAStartingLobbyCanBecomeStarted()
    {
        // A committed outbox row is a durable *request*, not a created match (Risk #6). Reaching Started requires
        // M8's MatchCreatedV1 — an Open lobby cannot jump straight there.
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.MarkStarted().Should().Be(LobbyOutcome.NotStartable);

        lobby.BeginStarting(_host, Now).Should().Be(LobbyOutcome.Ok);
        lobby.MarkStarted().Should().Be(LobbyOutcome.Ok);
        lobby.State.Should().Be(LobbyState.Started);
    }

    [Fact]
    public void AStartedLobbyIsTerminalAndRejectsEveryMutation()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);
        lobby.BeginStarting(_host, Now);
        lobby.MarkStarted();

        lobby.Join(Guid.NewGuid(), Now).Should().Be(LobbyOutcome.Closed);
        lobby.Leave(_alice, Now).Outcome.Should().Be(LobbyOutcome.Closed);
        lobby.Kick(_host, _alice, Now).Should().Be(LobbyOutcome.Closed);
        lobby.SetReadiness(_alice, false, Now).Should().Be(LobbyOutcome.Closed);
        lobby.ChangeSettings(_host, LobbyTestFactory.Settings(), Now).Should().Be(LobbyOutcome.Closed);
    }

    [Fact]
    public void ARecoverableFailureReturnsTheLobbyToOpenAndPreservesReadinessByDefault()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);
        lobby.BeginStarting(_host, Now);

        lobby.ReturnToOpen(resetReadiness: false, Now).Should().Be(LobbyOutcome.Ok);

        lobby.State.Should().Be(LobbyState.Open);
        lobby.FindJoinedMember(_alice)!.IsReady.Should().BeTrue();
    }

    [Fact]
    public void AFailureThatIdentifiesStaleStateResetsReadiness()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);
        lobby.BeginStarting(_host, Now);

        lobby.ReturnToOpen(resetReadiness: true, Now).Should().Be(LobbyOutcome.Ok);

        lobby.State.Should().Be(LobbyState.Open);
        lobby.FindJoinedMember(_alice)!.IsReady.Should().BeFalse();
    }

    [Fact]
    public void ALobbyThatExpiredWhileStartingDoesNotSilentlyReopen()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);
        lobby.BeginStarting(_host, Now);

        _clock.Advance(TimeSpan.FromHours(2));

        lobby.ReturnToOpen(resetReadiness: false, Now).Should().Be(LobbyOutcome.Expired);
        lobby.State.Should().Be(LobbyState.Expired);
    }

    // ── Settings validation ──────────────────────────────────────────────────

    [Fact]
    public void ALobbyCannotBeCreatedWithAnUnresolvedRegion()
    {
        // "Auto" is a request-time input; persisting it would partition the matchmaking queue on a non-region.
        var act = () => LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(resolvedRegion: "Auto"));

        act.Should().Throw<ArgumentException>().WithMessage("*Auto*");
    }

    [Fact]
    public void ALobbyCannotBeCreatedWithAnUnknownTimeControl()
    {
        var act = () => LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(timeControlId: "hyperbullet-0-1"));

        act.Should().Throw<ArgumentException>().WithMessage("*allow-list*");
    }

    [Fact]
    public void ALobbyCannotBeCreatedWithAnUnknownTieBreakRule()
    {
        var act = () => LobbyTestFactory.Open(_host, Now, LobbyTestFactory.Settings(tieBreakRuleId: "coin-flip"));

        act.Should().Throw<ArgumentException>().WithMessage("*allow-list*");
    }
}
