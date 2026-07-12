using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.DTOs;
using SimPle.Application.Matchmaking.Services;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Users;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The Quick Match ticket surface: validation, the cross-table one-active-lobby-or-ticket invariant, privacy-safe
/// not-found, honest rating provenance, and the cancel-versus-claim race.
/// </summary>
public sealed class MatchmakingServiceTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly IMatchmakingRepository _tickets = Substitute.For<IMatchmakingRepository>();
    private readonly ILobbyRepository _lobbies = Substitute.For<ILobbyRepository>();
    private readonly IMatchRuntimeProbe _matchRuntime = Substitute.For<IMatchRuntimeProbe>();
    private readonly IChatRuntimeProbe _chatRuntime = Substitute.For<IChatRuntimeProbe>();
    private readonly IAiParticipantProbe _aiProbe = Substitute.For<IAiParticipantProbe>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private readonly MatchmakingService _sut;

    public MatchmakingServiceTests()
    {
        // The true state of the platform at Module 6: nothing queued, no lobby, no match runtime.
        _tickets.GetActiveTicketForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MatchmakingTicket?)null);
        _lobbies.GetActiveLobbyForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Lobby?)null);
        _lobbies.GetCapabilityProfileAsync("chess-lite", 1, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Profile());
        _lobbies.GetGameAsync("chess-lite", Arg.Any<CancellationToken>()).Returns(AvailableGame());
        _lobbies.GetGameModesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "multiplayer", "ranked", "ai" });

        _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => MakeUser(call.Arg<Guid>()));

        _matchRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        _matchRuntime.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _chatRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        _aiProbe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        _sut = new MatchmakingService(
            _tickets, _lobbies, new PassThroughCommandRunner(),
            _matchRuntime, _chatRuntime, _aiProbe, _users,
            Options.Create(new LobbyCredentialOptions { Key = new string('k', 32), DefaultRegion = "eu-west" }),
            _clock,
            NullLogger<MatchmakingService>.Instance);
    }

    // ── Enqueue works before Module 8 ────────────────────────────────────────

    [Fact]
    public async Task Enqueue_SucceedsWithNoMatchRuntime_AndSaysSoInDependencyReadiness()
    {
        // The spec is explicit that ticket create/status/cancel and expiry remain fully functional before M8. The
        // honesty lives in dependencyReadiness, not in a 503: the player queues, watches the band widen, and times
        // out truthfully. Refusing to enqueue would be a different lie — that the feature does not exist.
        var result = await _sut.EnqueueAsync(_actor, Request());

        result.IsSuccess.Should().BeTrue();
        result.Value!.State.Should().Be("Queued");
        result.Value.DependencyReadiness.MatchRuntime.Should().BeFalse();

        await _tickets.Received(1).AddTicketAsync(
            Arg.Is<MatchmakingTicket>(t => t.UserId == _actor && t.GameSlug == "chess-lite"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_AlwaysSnapshotsTheProvisionalRating_NeverAClientSuppliedOne()
    {
        // M10 does not exist. The request DTO has no rating field at all — a client that could name its own rating
        // could choose its own opponents.
        var result = await _sut.EnqueueAsync(_actor, Request());

        result.Value!.Rating.Should().Be(1200);
        result.Value.RatingSourceVersion.Should().Be("provisional-1200-v1");
    }

    [Fact]
    public async Task Enqueue_ResolvesTheRegion_AndNeverPersistsAuto()
    {
        var result = await _sut.EnqueueAsync(_actor, Request() with { Region = "Auto" });

        result.IsSuccess.Should().BeTrue();
        result.Value!.ResolvedRegion.Should().Be("eu-west");
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task Enqueue_RejectsAPlayerCountOutsideTheSupportedRange(int playerCount)
    {
        // Quick Match is a multiplayer queue by definition: a one-player "match" has nobody to find.
        var result = await _sut.EnqueueAsync(_actor, Request() with { PlayerCount = playerCount });

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
    }

    [Fact]
    public async Task Enqueue_RejectsAnUnknownMode()
    {
        var result = await _sut.EnqueueAsync(_actor, Request() with { Mode = "not-a-mode" });

        result.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
    }

    [Fact]
    public async Task Enqueue_RejectsAnUnknownTimeControl()
    {
        var result = await _sut.EnqueueAsync(_actor, Request() with { TimeControlId = "not-a-time-control" });

        result.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
    }

    // ── Capability ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_FailsClosedWhenThePinnedCapabilityVersionDoesNotExist()
    {
        _lobbies.GetCapabilityProfileAsync("chess-lite", 99, Arg.Any<CancellationToken>())
            .Returns((SimPle.Domain.Capabilities.GameCapabilityProfile?)null);

        var result = await _sut.EnqueueAsync(_actor, Request() with { CapabilityVersion = 99 });

        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);
    }

    [Fact]
    public async Task Enqueue_FailsClosedWhenTheProfileIsDeactivatedUnderneathIt()
    {
        var profile = LobbyTestFactory.Profile();
        profile.Deactivate();
        _lobbies.GetCapabilityProfileAsync("chess-lite", 1, Arg.Any<CancellationToken>()).Returns(profile);

        var result = await _sut.EnqueueAsync(_actor, Request());

        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);
    }

    [Fact]
    public async Task Enqueue_FailsClosedWhenTheGameIsNotAvailable()
    {
        _lobbies.GetGameAsync("chess-lite", Arg.Any<CancellationToken>()).Returns(RetiredGame());

        var result = await _sut.EnqueueAsync(_actor, Request());

        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);
    }

    [Fact]
    public async Task Enqueue_RejectsARatedTicketForAGameThatIsNotRatedEligible()
    {
        _lobbies.GetCapabilityProfileAsync("chess-lite", 1, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Profile(
                allowedModes: new[] { "multiplayer" }, ratedEligible: false, aiFillEligible: false));

        var result = await _sut.EnqueueAsync(_actor, Request() with { Rated = true });

        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);
    }

    // ── One active lobby OR one active ticket ────────────────────────────────

    [Fact]
    public async Task Enqueue_IsIdempotent_AnIdenticalLiveTicketIsReturnedRatherThanASecondBeingMinted()
    {
        // The ticket entity has no idempotency-key column by design (the spec's data model gives one only to
        // LobbyStartRequest), so the pool key *is* the key. A retried request must not be punished for a flaky
        // network by being told it is "already queued".
        var live = TicketFactory.Queued(T0, userId: _actor);
        _tickets.GetActiveTicketForUserAsync(_actor, Arg.Any<CancellationToken>()).Returns(live);

        var result = await _sut.EnqueueAsync(_actor, Request());

        result.IsSuccess.Should().BeTrue();
        result.Value!.TicketId.Should().Be(live.Id);

        await _tickets.DidNotReceive().AddTicketAsync(Arg.Any<MatchmakingTicket>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_RejectsADifferentTicketWhileOneIsLive()
    {
        var live = TicketFactory.Queued(T0, userId: _actor, gameSlug: "chess-lite");
        _tickets.GetActiveTicketForUserAsync(_actor, Arg.Any<CancellationToken>()).Returns(live);

        var result = await _sut.EnqueueAsync(_actor, Request() with { TimeControlId = "rapid-10-0" });

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(MatchmakingErrors.AlreadyQueued);
    }

    [Fact]
    public async Task Enqueue_RejectsAUserWhoIsAlreadyInALobby()
    {
        // The cross-table half of the invariant. The two filtered unique indexes cannot see each other, so this
        // check inside the command is what covers the gap (Risk #2).
        _lobbies.GetActiveLobbyForUserAsync(_actor, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Open(_actor, T0));

        var result = await _sut.EnqueueAsync(_actor, Request());

        result.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
        await _tickets.DidNotReceive().AddTicketAsync(Arg.Any<MatchmakingTicket>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enqueue_RejectsAUserAlreadyInALiveMatch()
    {
        _matchRuntime.IsInActiveMatchAsync(_actor, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.EnqueueAsync(_actor, Request());

        result.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
    }

    // ── Status: privacy-safe not-found (OWASP API1:2023) ─────────────────────

    [Fact]
    public async Task GetTicket_AnotherUsersTicketIdIsIndistinguishableFromOneThatNeverExisted()
    {
        // A 403 here would confirm the id exists. That is the BOLA leak the 404 closes.
        var theirs = TicketFactory.Queued(T0, userId: _stranger);
        _tickets.GetTicketAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);
        _tickets.GetTicketAsync(Arg.Is<Guid>(id => id != theirs.Id), Arg.Any<CancellationToken>())
            .Returns((MatchmakingTicket?)null);

        var foreign = await _sut.GetTicketAsync(_actor, theirs.Id);
        var missing = await _sut.GetTicketAsync(_actor, Guid.NewGuid());

        foreign.Error!.Code.Should().Be(MatchmakingErrors.TicketNotFound);
        missing.Error!.Code.Should().Be(MatchmakingErrors.TicketNotFound);
        foreign.Error.Code.Should().Be(missing.Error.Code);
    }

    [Fact]
    public async Task GetTicket_ReportsTheBandWideningAsTheTicketAges()
    {
        var ticket = TicketFactory.Queued(T0, userId: _actor);
        _tickets.GetTicketAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        (await _sut.GetTicketAsync(_actor, ticket.Id)).Value!.CurrentBand.Should().Be(100);

        _clock.SetUtcNow(T0.AddSeconds(15));
        (await _sut.GetTicketAsync(_actor, ticket.Id)).Value!.CurrentBand.Should().Be(200);

        _clock.SetUtcNow(T0.AddSeconds(30));
        (await _sut.GetTicketAsync(_actor, ticket.Id)).Value!.CurrentBand.Should().Be(400);

        // At the deadline there is no band — there is a terminal outcome.
        _clock.SetUtcNow(T0.AddSeconds(60));
        (await _sut.GetTicketAsync(_actor, ticket.Id)).Value!.CurrentBand.Should().BeNull();
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_WhileQueued_Commits()
    {
        var ticket = TicketFactory.Queued(T0, userId: _actor);
        _tickets.GetTicketForUpdateAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        var result = await _sut.CancelAsync(_actor, ticket.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.State.Should().Be("Cancelled");
        ticket.State.Should().Be(MatchmakingTicketState.Cancelled);
    }

    [Fact]
    public async Task Cancel_AfterAWorkerClaim_IsNotAnError_ItReturnsTheCurrentStatus()
    {
        // Matchmaking.CancelTooLate is a 200, not a 4xx. The user pressed cancel in good faith and the queue simply
        // got there first — reporting that as a failure would be blaming them for losing a race they cannot see.
        var ticket = TicketFactory.Queued(T0, userId: _actor);
        ticket.Claim("worker-1", T0);
        _tickets.GetTicketForUpdateAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        var result = await _sut.CancelAsync(_actor, ticket.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.State.Should().Be("Claimed");
        ticket.State.Should().Be(MatchmakingTicketState.Claimed);   // unchanged

        await _tickets.DidNotReceive().SaveAsync(
            Arg.Any<IReadOnlyList<SimPle.Domain.Outbox.OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_AnotherUsersTicketIsAPrivacySafeNotFound()
    {
        var theirs = TicketFactory.Queued(T0, userId: _stranger);
        _tickets.GetTicketForUpdateAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(theirs);

        var result = await _sut.CancelAsync(_actor, theirs.Id);

        result.Error!.Code.Should().Be(MatchmakingErrors.TicketNotFound);
        theirs.State.Should().Be(MatchmakingTicketState.Queued);   // untouched
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static CreateTicketRequestDto Request() => new(
        GameSlug: "chess-lite",
        CapabilityVersion: 1,
        Mode: "multiplayer",
        PlayerCount: 2,
        TimeControlId: "blitz-3-2",
        Rated: false,
        Region: null);

    private static Game AvailableGame() => GameWithLifecycle(GameLifecycle.Available);

    private static Game RetiredGame() => GameWithLifecycle(GameLifecycle.Retired);

    private static Game GameWithLifecycle(GameLifecycle lifecycle) => Game.Create(
        slug: "chess-lite", name: "Chess Lite", summary: "A streamlined chess experience.",
        rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
        estimatedDurationMinMinutes: 10, estimatedDurationMaxMinutes: 20,
        minPlayers: 2, maxPlayers: 4, initialLifecycle: lifecycle,
        featuredRank: null, sortOrder: 1, artToken: "chess-lite",
        artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
        manifestVersion: "2026.1", category: "strategy",
        tags: new[] { "classic", "logic" },
        modes: new[] { "multiplayer", "ranked", "ai" });

    private static User MakeUser(Guid id)
    {
        var user = User.Create($"user{id:N}"[..12], $"{id:N}@example.com", "hash", "Test Player");
        typeof(SimPle.Domain.Common.Entity)
            .GetProperty(nameof(SimPle.Domain.Common.Entity.Id))!
            .SetValue(user, id);
        return user;
    }
}
