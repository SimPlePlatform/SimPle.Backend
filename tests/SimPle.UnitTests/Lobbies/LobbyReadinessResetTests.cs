using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SimPle.Domain.Lobbies;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Readiness-reset scope (brief Risk #4): "every match-affecting settings change and every join/leave/kick resets
/// readiness for all joined non-host humans; the host is implicitly ready."
///
/// Missing a single reset trigger lets a stale-ready roster start on changed settings — which is exactly the bug
/// that is invisible until someone is dropped into a match they never agreed to. There is one test per trigger.
/// </summary>
public class LobbyReadinessResetTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    private static bool ReadinessOf(Lobby lobby, Guid userId) =>
        lobby.FindJoinedMember(userId)!.IsReady;

    // ── The host is implicitly ready ─────────────────────────────────────────

    [Fact]
    public void TheHostIsReadyFromTheMomentTheLobbyExists()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);

        ReadinessOf(lobby, _host).Should().BeTrue();
    }

    [Fact]
    public void TheHostCannotUnReadyThemselves()
    {
        // Letting the host clear their own readiness would create a lobby that can never satisfy IsEveryoneReady.
        var lobby = LobbyTestFactory.Open(_host, Now);

        var outcome = lobby.SetReadiness(_host, false, Now);

        outcome.Should().Be(LobbyOutcome.InvalidTarget);
        ReadinessOf(lobby, _host).Should().BeTrue();
    }

    [Fact]
    public void AResetNeverClearsTheHostsReadiness()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.Join(_bob, Now);   // a reset trigger

        ReadinessOf(lobby, _host).Should().BeTrue("the host is implicitly ready and is never counted in a reset");
        ReadinessOf(lobby, _alice).Should().BeFalse();
    }

    // ── Trigger: join ────────────────────────────────────────────────────────

    [Fact]
    public void AJoinResetsEveryJoinedNonHostHuman()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);
        ReadinessOf(lobby, _alice).Should().BeTrue();

        lobby.Join(_bob, Now);

        ReadinessOf(lobby, _alice).Should().BeFalse("a new member changes the roster the others agreed to");
        ReadinessOf(lobby, _bob).Should().BeFalse("a joiner starts un-ready");
    }

    // ── Trigger: leave ───────────────────────────────────────────────────────

    [Fact]
    public void ALeaveResetsTheRemainingNonHostMembers()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice, _bob);

        lobby.Leave(_bob, Now);

        ReadinessOf(lobby, _alice).Should().BeFalse();
    }

    // ── Trigger: kick ────────────────────────────────────────────────────────

    [Fact]
    public void AKickResetsTheRemainingNonHostMembers()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice, _bob);

        lobby.Kick(_host, _bob, Now);

        ReadinessOf(lobby, _alice).Should().BeFalse();
    }

    // ── Trigger: match-affecting settings changes ────────────────────────────

    public static TheoryData<string, LobbySettings> MatchAffectingChanges() => new()
    {
        { "game", LobbyTestFactory.Settings(gameSlug: "checkers") },
        { "capability version", LobbyTestFactory.Settings(capabilityVersion: 2) },
        { "seat count", LobbyTestFactory.Settings(maxPlayers: 3) },
        { "time control", LobbyTestFactory.Settings(timeControlId: "rapid-10-0") },
        { "rated", LobbyTestFactory.Settings(rated: true) },
        { "region", LobbyTestFactory.Settings(resolvedRegion: "us-east") },
        { "tie-break rule", LobbyTestFactory.Settings(tieBreakRuleId: "sudden-death") },
        { "AI fill", LobbyTestFactory.Settings(aiFillRequested: true) },
    };

    [Theory]
    [MemberData(nameof(MatchAffectingChanges))]
    public void AMatchAffectingSettingsChangeResetsReadiness(string changed, LobbySettings settings)
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        var outcome = lobby.ChangeSettings(_host, settings, Now);

        outcome.Should().Be(LobbyOutcome.Ok);
        ReadinessOf(lobby, _alice).Should()
            .BeFalse($"changing the {changed} changes what match gets played, so a ready roster is stale");
    }

    // ── Non-triggers: access-control settings ────────────────────────────────

    [Fact]
    public void ChangingPrivacyAloneDoesNotResetReadiness()
    {
        // Privacy governs who may reach the lobby, not what is played — it cannot make a ready roster stale.
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.ChangeSettings(_host, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public), Now);

        ReadinessOf(lobby, _alice).Should().BeTrue();
    }

    [Fact]
    public void ChangingSpectatorPolicyAloneDoesNotResetReadiness()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.ChangeSettings(_host, LobbyTestFactory.Settings(spectatorPolicy: SpectatorPolicy.Disabled), Now);

        ReadinessOf(lobby, _alice).Should().BeTrue();
    }

    [Fact]
    public void ReapplyingTheIdenticalSettingsDoesNotResetReadiness()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, Now, _alice);

        lobby.ChangeSettings(_host, lobby.CurrentSettings, Now);

        ReadinessOf(lobby, _alice).Should().BeTrue("nothing about the match changed");
    }

    // ── Every mutation bumps the revision ────────────────────────────────────

    [Fact]
    public void EveryMutationBumpsTheRevision()
    {
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Revision.Should().Be(1, "a freshly created lobby is at revision 1");

        lobby.Join(_alice, Now);
        lobby.Revision.Should().Be(2);

        lobby.SetReadiness(_alice, true, Now);
        lobby.Revision.Should().Be(3);

        lobby.ChangeSettings(_host, LobbyTestFactory.Settings(timeControlId: "rapid-10-0"), Now);
        lobby.Revision.Should().Be(4);

        lobby.Kick(_host, _alice, Now);
        lobby.Revision.Should().Be(5);
    }

    [Fact]
    public void ARejectedMutationDoesNotBumpTheRevision()
    {
        // A stale-revision conflict is only meaningful if a *failed* command does not itself move the revision.
        var lobby = LobbyTestFactory.Open(_host, Now);
        lobby.Join(_alice, Now);
        var revisionBefore = lobby.Revision;

        lobby.Kick(_alice, _host, Now).Should().Be(LobbyOutcome.Forbidden);   // alice is not the host

        lobby.Revision.Should().Be(revisionBefore);
    }
}
