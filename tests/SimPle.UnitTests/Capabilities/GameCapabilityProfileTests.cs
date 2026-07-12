using FluentAssertions;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Lobbies;
using SimPle.UnitTests.Lobbies;

namespace SimPle.UnitTests.Capabilities;

/// <summary>
/// The capability profile (D2) is what turns the brief's "stale/unsupported combinations fail before persistence"
/// into a real, testable path. Module 4's catalog has no capability version, time controls, tie-breaks, spectator
/// policy, or rated flag, so without this table "capability disabled after create" could not be triggered at all.
/// </summary>
public class GameCapabilityProfileTests
{
    // ── Permits: the happy path and each rejection reason ────────────────────

    [Fact]
    public void AProfilePermitsSettingsItDeclares()
    {
        var profile = LobbyTestFactory.Profile();

        var check = profile.Permits(LobbyTestFactory.Settings(timeControlId: "blitz-3-2", tieBreakRuleId: "none"));

        check.Allowed.Should().BeTrue();
        check.Reason.Should().BeNull();
    }

    [Fact]
    public void ADeactivatedProfileRejectsEverything()
    {
        // This is the "capability disabled after create" path: a lobby pinned to v1 keeps existing, but every new
        // command that depends on the pin fails closed.
        var profile = LobbyTestFactory.Profile();
        profile.Deactivate();

        var check = profile.Permits(LobbyTestFactory.Settings());

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("no longer active");
    }

    [Fact]
    public void DeactivationIsIdempotent()
    {
        var profile = LobbyTestFactory.Profile();
        profile.Deactivate();
        profile.Deactivate();

        profile.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AProfileRejectsSettingsForADifferentGame()
    {
        var profile = LobbyTestFactory.Profile(gameSlug: "chess-lite");

        var check = profile.Permits(LobbyTestFactory.Settings(gameSlug: "checkers"));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("different game");
    }

    [Fact]
    public void AProfileRejectsSettingsPinnedToADifferentCapabilityVersion()
    {
        var profile = LobbyTestFactory.Profile(capabilityVersion: 1);

        var check = profile.Permits(LobbyTestFactory.Settings(capabilityVersion: 2));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("different capability version");
    }

    [Fact]
    public void AProfileRejectsAnUnsupportedTimeControl()
    {
        var profile = LobbyTestFactory.Profile(timeControls: new[] { "blitz-3-2" });

        var check = profile.Permits(LobbyTestFactory.Settings(timeControlId: "classical-30-0"));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("classical-30-0");
    }

    [Fact]
    public void AProfileRejectsAnUnsupportedTieBreakRule()
    {
        var profile = LobbyTestFactory.Profile(tieBreakRules: new[] { "none" });

        var check = profile.Permits(LobbyTestFactory.Settings(tieBreakRuleId: "sudden-death"));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("sudden-death");
    }

    [Fact]
    public void AProfileRejectsAnUnsupportedSpectatorPolicy()
    {
        var profile = LobbyTestFactory.Profile(spectatorPolicies: new[] { "Disabled" });

        var check = profile.Permits(LobbyTestFactory.Settings(spectatorPolicy: SpectatorPolicy.Anyone));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("Anyone");
    }

    [Fact]
    public void AProfileRejectsSeatCountsOutsideItsBounds()
    {
        var profile = LobbyTestFactory.Profile(minPlayers: 2, maxPlayers: 2);

        profile.Permits(LobbyTestFactory.Settings(maxPlayers: 4)).Allowed.Should().BeFalse();
        profile.Permits(LobbyTestFactory.Settings(maxPlayers: 2)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void AProfileThatIsNotRatedEligibleRejectsARatedLobby()
    {
        var profile = LobbyTestFactory.Profile(
            allowedModes: new[] { "multiplayer" }, ratedEligible: false, aiFillEligible: false);

        var check = profile.Permits(LobbyTestFactory.Settings(rated: true));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("rated");
    }

    [Fact]
    public void AProfileThatIsNotAiFillEligibleRejectsAnAiFillRequest()
    {
        var profile = LobbyTestFactory.Profile(
            allowedModes: new[] { "multiplayer" }, ratedEligible: false, aiFillEligible: false);

        var check = profile.Permits(LobbyTestFactory.Settings(aiFillRequested: true));

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("AI fill");
    }

    // ── Construction invariants ──────────────────────────────────────────────

    [Fact]
    public void RatedEligibilityRequiresTheRankedMode()
    {
        // A "rated" game with no ranked mode is incoherent — there is nothing to be rated at.
        var act = () => LobbyTestFactory.Profile(
            allowedModes: new[] { "multiplayer" }, ratedEligible: true, aiFillEligible: false);

        act.Should().Throw<ArgumentException>().WithMessage("*ranked*");
    }

    [Fact]
    public void AiFillEligibilityRequiresTheAiMode()
    {
        var act = () => LobbyTestFactory.Profile(
            allowedModes: new[] { "multiplayer" }, ratedEligible: false, aiFillEligible: true);

        act.Should().Throw<ArgumentException>().WithMessage("*ai*");
    }

    [Fact]
    public void AProfileMustSeatAtLeastTwoPlayers()
    {
        // A lobby is a multiplayer surface; a game's solo mode is reachable from the library, not from a lobby.
        var act = () => LobbyTestFactory.Profile(minPlayers: 1);

        act.Should().Throw<ArgumentException>().WithMessage("*at least 2*");
    }

    [Fact]
    public void AProfileRejectsATimeControlOutsideThePlatformAllowList()
    {
        var act = () => LobbyTestFactory.Profile(timeControls: new[] { "hyperbullet-0-1" });

        act.Should().Throw<ArgumentException>().WithMessage("*allow-list*");
    }

    [Fact]
    public void AProfileRejectsAModeOutsideModule4sAllowList()
    {
        // Reuses GameCatalogAllowLists.Modes rather than keeping a parallel copy that could drift from M4's.
        var act = () => LobbyTestFactory.Profile(allowedModes: new[] { "battle-royale" });

        act.Should().Throw<ArgumentException>().WithMessage("*allow-list*");
    }

    [Fact]
    public void AProfileRejectsDuplicateValues()
    {
        var act = () => LobbyTestFactory.Profile(timeControls: new[] { "blitz-3-2", "blitz-3-2" });

        act.Should().Throw<ArgumentException>().WithMessage("*duplicate*");
    }

    // ── Catalog drift ────────────────────────────────────────────────────────

    [Fact]
    public void AProfileThatMatchesTheCatalogDoesNotContradictIt()
    {
        var profile = LobbyTestFactory.Profile(
            minPlayers: 2, maxPlayers: 2, allowedModes: new[] { "multiplayer", "ranked", "ai" });

        var check = profile.ContradictsCatalog(2, 2, new[] { "ai", "multiplayer", "ranked" });

        check.Allowed.Should().BeTrue();
    }

    [Fact]
    public void AProfileWiderThanTheCatalogsSeatBoundsIsDrift()
    {
        // Would let a lobby be created that M5's engine cannot host. Fail closed at seed time rather than at a
        // player's Start click.
        var profile = LobbyTestFactory.Profile(minPlayers: 2, maxPlayers: 8);

        var check = profile.ContradictsCatalog(2, 4, new[] { "multiplayer", "ranked", "ai" });

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("exceed the catalog");
    }

    [Fact]
    public void AProfileAllowingAModeTheCatalogDoesNotIsDrift()
    {
        var profile = LobbyTestFactory.Profile(
            allowedModes: new[] { "multiplayer", "ranked", "ai" });

        var check = profile.ContradictsCatalog(2, 4, new[] { "multiplayer" });

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("ranked");
    }

    [Fact]
    public void AProfileNarrowerThanTheCatalogIsFine()
    {
        // A subset is exactly what a profile is meant to be: the catalog says what the game *can* do, the profile
        // says what a lobby *may configure*.
        var profile = LobbyTestFactory.Profile(
            minPlayers: 2, maxPlayers: 2, allowedModes: new[] { "multiplayer" },
            ratedEligible: false, aiFillEligible: false);

        var check = profile.ContradictsCatalog(1, 8, new[] { "solo", "multiplayer", "ranked", "ai" });

        check.Allowed.Should().BeTrue();
    }
}

/// <summary>
/// Server-side region resolution. <c>User.Region</c> is free text validated only for length, so it is treated as
/// untrusted input rather than a trusted region.
/// </summary>
public class LobbyRegionTests
{
    [Fact]
    public void AnExplicitAllowListedRequestWins()
    {
        LobbyRegion.Resolve("us-east", "eu-west", "eu-central").Should().Be("us-east");
    }

    [Fact]
    public void AutoFallsBackToTheUsersProfileRegion()
    {
        LobbyRegion.Resolve(LobbyRegion.Auto, "ap-south", "eu-west").Should().Be("ap-south");
    }

    [Fact]
    public void AutoFallsBackToTheDeploymentDefaultWhenTheProfileHasNoRegion()
    {
        LobbyRegion.Resolve(LobbyRegion.Auto, null, "eu-west").Should().Be("eu-west");
    }

    [Fact]
    public void AGarbageProfileRegionFallsThroughToTheDeploymentDefault()
    {
        // User.Region is free text (UpdateProfileRequestValidator checks length only). A profile carrying "Narnia"
        // must not partition the matchmaking queue into a pool of one.
        LobbyRegion.Resolve(LobbyRegion.Auto, "Narnia", "eu-west").Should().Be("eu-west");
    }

    [Fact]
    public void AGarbageRequestedRegionFallsThroughRatherThanBeingPersisted()
    {
        LobbyRegion.Resolve("mars-north", "ap-south", "eu-west").Should().Be("ap-south");
    }

    [Fact]
    public void ResolveNeverReturnsAuto()
    {
        LobbyRegion.Resolve(LobbyRegion.Auto, LobbyRegion.Auto, "eu-west").Should().Be("eu-west");
        LobbyRegion.IsResolved(LobbyRegion.Resolve(LobbyRegion.Auto, null, "eu-west")).Should().BeTrue();
    }

    [Fact]
    public void AMisconfiguredDeploymentDefaultFailsLoudly()
    {
        // Every fallback path ends at the deployment default. If that is itself not a real region, silently
        // returning it would poison every lobby and ticket in the deployment.
        var act = () => LobbyRegion.Resolve(LobbyRegion.Auto, null, "not-a-region");

        act.Should().Throw<ArgumentException>().WithMessage("*not allow-listed*");
    }

    [Fact]
    public void AutoIsNeverConsideredAResolvedRegion()
    {
        LobbyRegion.IsResolved(LobbyRegion.Auto).Should().BeFalse();
        LobbyRegion.IsResolved("eu-west").Should().BeTrue();
        LobbyRegion.IsResolved("narnia").Should().BeFalse();
    }
}
