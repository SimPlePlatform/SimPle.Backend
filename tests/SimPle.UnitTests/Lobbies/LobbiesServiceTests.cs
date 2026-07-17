using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.GameHost.Services;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Services;
using SimPle.Domain.Capabilities;
using SimPle.Domain.GameHost;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;
using SimPle.Shared.Common;
using Xunit;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Command-layer tests for <see cref="LobbiesService"/>: authorization, privacy-safe not-found, capability
/// validation, credential-oracle prevention, honest deferral, and the failed-join throttle.
///
/// The <see cref="ILobbyCommandRunner"/> is a pass-through here. That is deliberate, not a shortcut: the retry it
/// performs only means anything against a database that actually enforces unique indexes and row versions, so it
/// is proven in <c>LobbiesPostgresConcurrencyTests</c> against real PostgreSQL. Substituting a fake that
/// "simulated" contention would prove only that the fake works.
/// </summary>
public sealed class LobbiesServiceTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly ILobbyRepository _repo = Substitute.For<ILobbyRepository>();
    private readonly ILobbyCredentialHasher _hasher = Substitute.For<ILobbyCredentialHasher>();
    private readonly ILobbyJoinThrottle _throttle = Substitute.For<ILobbyJoinThrottle>();
    private readonly IMatchRuntimeProbe _matchRuntime = Substitute.For<IMatchRuntimeProbe>();
    private readonly IChatRuntimeProbe _chatRuntime = Substitute.For<IChatRuntimeProbe>();
    private readonly IAiParticipantProbe _aiProbe = Substitute.For<IAiParticipantProbe>();
    private readonly IGameRegistry _engines = Substitute.For<IGameRegistry>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly FakeTimeProvider _clock = new(T0);

    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _joiner = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private readonly LobbiesService _sut;

    public LobbiesServiceTests()
    {
        // Default world: nothing blocked, nobody already active, one available game with a permissive profile,
        // and no downstream module registered — the true state of the platform at Module 6.
        _repo.GetBlockedCounterpartsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());
        _repo.GetActiveLobbyForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Lobby?)null);
        _repo.GetActiveTicketIdForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        _repo.AreFriendsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _repo.GetCapabilityProfileAsync("chess-lite", 1, Arg.Any<CancellationToken>()).Returns(Profile());
        _repo.GetGameAsync("chess-lite", Arg.Any<CancellationToken>()).Returns(AvailableGame());
        _repo.GetGameModesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { "multiplayer", "cooperative" });
        _repo.GetUsersAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Roster(call.Arg<IReadOnlyList<Guid>>()));

        _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => MakeUser(call.Arg<Guid>()));

        _hasher.HashCode(Arg.Any<string>()).Returns(call => "hash:" + call.Arg<string>());
        _hasher.HashLinkToken(Arg.Any<string>()).Returns(call => "hash:" + call.Arg<string>());

        _throttle.GetRetryAfterUtcAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DateTime?)null);

        _matchRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        _matchRuntime.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _chatRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        _aiProbe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        _engines.RegisteredDefinitions.Returns(Array.Empty<GameDefinitionMetadata>());

        _sut = new LobbiesService(
            _repo, new PassThroughRunner(), _hasher, _throttle, _matchRuntime, _chatRuntime, _aiProbe,
            _engines, _users, _storage,
            Options.Create(new StorageOptions()),
            Options.Create(new LobbyCredentialOptions { Key = new string('k', 32), DefaultRegion = "eu-west" }),
            _clock,
            NullLogger<LobbiesService>.Instance);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_IssuesACredentialAndSeatsTheHostReady()
    {
        var result = await _sut.CreateAsync(_host, CreateRequest());

        result.IsSuccess.Should().BeTrue();
        var value = result.Value!;

        value.Lobby.HostUserId.Should().Be(_host);
        value.Lobby.Seats.Should().ContainSingle()
            .Which.Should().Match<LobbySeatDto>(s => s.IsHost && s.IsReady);

        // The plaintext is handed back exactly once, here. It is never stored — only its digest is.
        value.Credential.Code.Should().NotBeNullOrWhiteSpace();
        value.Credential.LinkToken.Should().NotBeNullOrWhiteSpace();
        value.Credential.Generation.Should().Be(1);

        await _repo.Received(1).AddLobbyAsync(
            Arg.Any<Lobby>(),
            Arg.Is<LobbyJoinCredential>(c =>
                c.CodeDigest != value.Credential.Code && c.LinkTokenDigest != value.Credential.LinkToken),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_ResolvesAutoRegionServerSide_AndNeverPersistsAuto()
    {
        var result = await _sut.CreateAsync(_host, CreateRequest() with { Region = "Auto" });

        result.IsSuccess.Should().BeTrue();
        result.Value!.Lobby.ResolvedRegion.Should().Be("eu-west");
        result.Value.Lobby.ResolvedRegion.Should().NotBe(LobbyRegion.Auto);
    }

    [Fact]
    public async Task Create_WhenPinnedProfileIsInactive_FailsBeforePersistence()
    {
        var inactive = Profile();
        inactive.Deactivate();
        _repo.GetCapabilityProfileAsync("chess-lite", 1, Arg.Any<CancellationToken>()).Returns(inactive);

        var result = await _sut.CreateAsync(_host, CreateRequest());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);

        // "Before persistence" is the claim, so the absence of the write is what the test actually asserts.
        await _repo.DidNotReceive().AddLobbyAsync(
            Arg.Any<Lobby>(), Arg.Any<LobbyJoinCredential>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WhenTheGameIsNotAvailable_FailsClosed()
    {
        _repo.GetGameAsync("chess-lite", Arg.Any<CancellationToken>())
            .Returns(GameWithLifecycle(GameLifecycle.Maintenance));

        var result = await _sut.CreateAsync(_host, CreateRequest());

        result.Error!.Code.Should().Be(LobbyErrors.CapabilityDisabled);
    }

    [Fact]
    public async Task Create_WhenAlreadyInALobby_IsRejected()
    {
        _repo.GetActiveLobbyForUserAsync(_host, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Open(_host, T0));

        var result = await _sut.CreateAsync(_host, CreateRequest());

        result.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
    }

    /// <summary>
    /// The cross-table half of the invariant. Two filtered unique indexes on different tables cannot see each
    /// other, so a queued ticket has to block a lobby create in the command layer or not at all.
    /// </summary>
    [Fact]
    public async Task Create_WhenAlreadyHoldingAMatchmakingTicket_IsRejected()
    {
        _repo.GetActiveTicketIdForUserAsync(_host, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        var result = await _sut.CreateAsync(_host, CreateRequest());

        result.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
    }

    // ── Privacy-safe not-found (BOLA) ────────────────────────────────────────

    [Fact]
    public async Task Get_APrivateLobbyTheCallerIsNotIn_IsIndistinguishableFromOneThatDoesNotExist()
    {
        var lobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        _repo.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var foreignResult = await _sut.GetAsync(_stranger, lobby.Id);
        var missingResult = await _sut.GetAsync(_stranger, Guid.NewGuid());

        // Same code, same message. A 403 here would confirm the id exists — that is the leak.
        foreignResult.Error!.Code.Should().Be(LobbyErrors.NotFound);
        missingResult.Error!.Code.Should().Be(LobbyErrors.NotFound);
        foreignResult.Error.Message.Should().Be(missingResult.Error.Message);
    }

    [Fact]
    public async Task Get_AnOpenPublicLobby_IsVisibleToANonMember()
    {
        var lobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));
        _repo.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.GetAsync(_stranger, lobby.Id);

        result.IsSuccess.Should().BeTrue();
        // A non-member gets no actions — visibility is not authorization.
        result.Value!.AllowedActions.Should().BeEmpty();
    }

    [Fact]
    public async Task Kick_ByANonMember_Returns404_NotForbidden()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_joiner, T0);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.KickAsync(_stranger, lobby.Id, new KickMemberRequestDto(_joiner, lobby.Revision));

        result.Error!.Code.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task Kick_ByAMemberWhoIsNotTheHost_IsForbidden_BecauseTheyCanAlreadySeeTheLobby()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_joiner, T0);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.KickAsync(_joiner, lobby.Id, new KickMemberRequestDto(_host, lobby.Revision));

        // 403 is correct here and 404 would be wrong: this caller is a seated member and already knows the lobby
        // exists. Hiding it would tell them nothing they do not know and would only obscure the real reason.
        result.Error!.Code.Should().Be(LobbyErrors.Forbidden);
    }

    // ── Credential join: the oracle must stay shut ───────────────────────────

    [Fact]
    public async Task Join_WithAValidCode_SeatsTheCallerUnready()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var credential = IssuedCredential(lobby.Id, "GOOD-CODE");
        _repo.FindActiveByCodeDigestAsync("hash:GOOD-CODE", Arg.Any<CancellationToken>()).Returns(credential);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("GOOD-CODE", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Seats.Should().HaveCount(2);
        result.Value.Seats.Single(s => !s.IsHost).IsReady.Should().BeFalse();

        await _throttle.Received(1).ClearAsync(_joiner, Arg.Any<CancellationToken>());
        await _throttle.DidNotReceive().RecordFailureAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The credential-oracle test. A wrong code, an expired one, a rotated one, and a closed lobby must be
    /// <em>byte-for-byte</em> the same answer — any difference is a probe an attacker can use to enumerate which
    /// lobbies and codes are live.
    /// </summary>
    [Fact]
    public async Task Join_WrongExpiredRotatedAndClosed_AllReturnTheIdenticalError()
    {
        // The three time-independent cases first: FakeTimeProvider refuses to rewind, so the expired case — which
        // is the only one that needs the clock moved — has to run last.

        // 1. Unknown code — no credential row at all.
        _repo.FindActiveByCodeDigestAsync("hash:WRONG", Arg.Any<CancellationToken>())
            .Returns((LobbyJoinCredential?)null);
        var wrong = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("WRONG", null));

        // 2. Rotated credential — superseded by a newer generation.
        var rotatedLobby = LobbyTestFactory.Open(_host, T0);
        var rotatedCred = IssuedCredential(rotatedLobby.Id, "ROTATED");
        rotatedCred.MarkRotated(T0);
        _repo.FindActiveByCodeDigestAsync("hash:ROTATED", Arg.Any<CancellationToken>()).Returns(rotatedCred);
        var rotated = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("ROTATED", null));

        // 3. Correct, live credential — but the lobby behind it has closed.
        var closedLobby = LobbyTestFactory.Open(_host, T0);
        closedLobby.Close(LobbyClosedReason.HostClosed, T0);
        var closedCred = IssuedCredential(closedLobby.Id, "CLOSED");
        _repo.FindActiveByCodeDigestAsync("hash:CLOSED", Arg.Any<CancellationToken>()).Returns(closedCred);
        _repo.GetForUpdateAsync(closedLobby.Id, Arg.Any<CancellationToken>()).Returns(closedLobby);
        var closed = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("CLOSED", null));

        // 4. Expired credential — the row exists and is still Active, but its 30 minutes are up.
        var expiredLobby = LobbyTestFactory.Open(_host, T0);
        var expiredCred = IssuedCredential(expiredLobby.Id, "EXPIRED");
        _repo.FindActiveByCodeDigestAsync("hash:EXPIRED", Arg.Any<CancellationToken>()).Returns(expiredCred);
        _repo.GetForUpdateAsync(expiredLobby.Id, Arg.Any<CancellationToken>()).Returns(expiredLobby);
        _clock.SetUtcNow(T0 + LobbyJoinCredential.Lifetime + TimeSpan.FromSeconds(1));
        var expired = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("EXPIRED", null));

        foreach (var result in new[] { wrong, expired, rotated, closed })
        {
            result.IsSuccess.Should().BeFalse();
            result.Error!.Code.Should().Be(LobbyErrors.CredentialInvalid);
            result.Error.Message.Should().Be(wrong.Error!.Message);
        }
    }

    [Fact]
    public async Task Join_WithABlockedMemberInTheLobby_IsRefused()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var credential = IssuedCredential(lobby.Id, "GOOD-CODE");
        _repo.FindActiveByCodeDigestAsync("hash:GOOD-CODE", Arg.Any<CancellationToken>()).Returns(credential);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _repo.GetBlockedCounterpartsAsync(_joiner, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { _host });

        var result = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("GOOD-CODE", null));

        result.Error!.Code.Should().Be(LobbyErrors.Blocked);
    }

    [Fact]
    public async Task Join_AFullLobby_ReturnsFull()
    {
        var lobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(maxPlayers: 2));
        lobby.Join(Guid.NewGuid(), T0);   // the second and last seat

        var credential = IssuedCredential(lobby.Id, "GOOD-CODE");
        _repo.FindActiveByCodeDigestAsync("hash:GOOD-CODE", Arg.Any<CancellationToken>()).Returns(credential);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("GOOD-CODE", null));

        result.Error!.Code.Should().Be(LobbyErrors.Full);
    }

    /// <summary>
    /// Only a bad credential feeds the throttle. Counting an already-in-a-lobby rejection would let an unrelated
    /// failure lock a legitimate user out of joining — and it would tell an attacker nothing anyway.
    /// </summary>
    [Fact]
    public async Task Join_OnlyAnInvalidCredentialCountsTowardTheFailureThrottle()
    {
        _repo.GetActiveLobbyForUserAsync(_joiner, Arg.Any<CancellationToken>())
            .Returns(LobbyTestFactory.Open(_joiner, T0));

        var lobby = LobbyTestFactory.Open(_host, T0);
        var credential = IssuedCredential(lobby.Id, "GOOD-CODE");
        _repo.FindActiveByCodeDigestAsync("hash:GOOD-CODE", Arg.Any<CancellationToken>()).Returns(credential);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var alreadyActive = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("GOOD-CODE", null));

        alreadyActive.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
        await _throttle.DidNotReceive().RecordFailureAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        _repo.FindActiveByCodeDigestAsync("hash:NOPE", Arg.Any<CancellationToken>())
            .Returns((LobbyJoinCredential?)null);
        await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("NOPE", null));

        await _throttle.Received(1).RecordFailureAsync(_joiner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_WhileThrottled_IsRejectedWithARetryAfter_BeforeAnyDigestWorkHappens()
    {
        var until = T0.AddMinutes(5);
        _throttle.GetRetryAfterUtcAsync(_joiner, Arg.Any<CancellationToken>()).Returns(until);

        var result = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("ANYTHING", null));

        result.Error!.Code.Should().Be(LobbyErrors.RateLimitExceeded);
        result.Error.RetryAfterUtc.Should().Be(until);

        // A throttled attacker must not even be able to time the comparison.
        await _repo.DidNotReceive().FindActiveByCodeDigestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_WithBothOrNeitherCredential_IsAValidationError()
    {
        var both = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto("CODE", "TOKEN"));
        var neither = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto(null, null));

        both.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
        neither.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
    }

    [Fact]
    public async Task Join_WithLobbyIdAlongsideACode_IsAValidationError()
    {
        var result = await _sut.JoinByCredentialAsync(
            _joiner, new JoinLobbyRequestDto("CODE", null, Guid.NewGuid()));

        result.Error!.Code.Should().Be(LobbyErrors.ValidationFailed);
    }

    // ── Join by id: only a Public+Open lobby, and it bypasses the credential throttle ────────

    [Fact]
    public async Task JoinByLobbyId_ForAPublicOpenLobby_SeatsTheCallerUnready()
    {
        var lobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto(null, null, lobby.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Seats.Should().HaveCount(2);
        result.Value.Seats.Single(s => !s.IsHost).IsReady.Should().BeFalse();

        // A resource id is not a secret: it never touches the failed-credential throttle.
        await _throttle.DidNotReceive().GetRetryAfterUtcAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _throttle.DidNotReceive().RecordFailureAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _throttle.DidNotReceive().ClearAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A private lobby's id, a closed lobby's id, and an unknown id must be byte-for-byte the same answer as the
    /// single-lobby read's privacy-safe 404 — this is not a second oracle alongside the credential one.
    /// </summary>
    [Fact]
    public async Task JoinByLobbyId_ForAPrivateClosedOrUnknownLobby_IsTheIdenticalNotFound()
    {
        var privateLobby = LobbyTestFactory.Open(_host, T0); // default settings: Private
        _repo.GetForUpdateAsync(privateLobby.Id, Arg.Any<CancellationToken>()).Returns(privateLobby);
        var private_ = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto(null, null, privateLobby.Id));

        var closedLobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));
        closedLobby.Close(LobbyClosedReason.HostClosed, T0);
        _repo.GetForUpdateAsync(closedLobby.Id, Arg.Any<CancellationToken>()).Returns(closedLobby);
        var closed = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto(null, null, closedLobby.Id));

        var unknownId = Guid.NewGuid();
        _repo.GetForUpdateAsync(unknownId, Arg.Any<CancellationToken>()).Returns((Lobby?)null);
        var unknown = await _sut.JoinByCredentialAsync(_joiner, new JoinLobbyRequestDto(null, null, unknownId));

        foreach (var result in new[] { private_, closed, unknown })
        {
            result.IsSuccess.Should().BeFalse();
            result.Error!.Code.Should().Be(LobbyErrors.NotFound);
            result.Error.Message.Should().Be(private_.Error!.Message);
        }
    }

    // ── Honest deferral: Start cannot succeed, and does not pretend it can ───

    [Fact]
    public async Task Start_WhileNoMatchRuntimeIsRegistered_Returns503_AndLeavesTheLobbyOpen()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, T0, _joiner);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.StartAsync(
            _host, lobby.Id, new StartLobbyRequestDto(lobby.Revision, "idem-1"));

        result.Error!.Code.Should().Be(LobbyErrors.MatchRuntimeUnavailable);

        // The two claims that actually matter: the lobby did not move, and no match request was committed.
        lobby.State.Should().Be(LobbyState.Open);
        await _repo.DidNotReceive().AddStartRequestAsync(
            Arg.Any<LobbyStartRequest>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Ordering check. With zero engines installed, checking M5 before M8 would answer
    /// <c>Lobbies.CapabilityDisabled</c> — blaming this lobby's configuration for a platform-wide absence of any
    /// runtime. The honest answer is the one the brief promises and the E2E asserts.
    /// </summary>
    [Fact]
    public async Task Start_WithNoEnginesInstalled_BlamesTheMissingRuntime_NotTheLobbysCapability()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, T0, _joiner);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _engines.RegisteredDefinitions.Returns(Array.Empty<GameDefinitionMetadata>());

        var result = await _sut.StartAsync(
            _host, lobby.Id, new StartLobbyRequestDto(lobby.Revision, "idem-1"));

        result.Error!.Code.Should().Be(LobbyErrors.MatchRuntimeUnavailable);
        result.Error.Code.Should().NotBe(LobbyErrors.CapabilityDisabled);
    }

    [Fact]
    public async Task Start_ByAMemberWhoIsNotTheHost_IsForbidden()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, T0, _joiner);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.StartAsync(
            _joiner, lobby.Id, new StartLobbyRequestDto(lobby.Revision, "idem-1"));

        result.Error!.Code.Should().Be(LobbyErrors.Forbidden);
    }

    [Fact]
    public async Task AllowedActions_OmitStart_WhileNoMatchRuntimeExists()
    {
        var lobby = LobbyTestFactory.ReadyLobby(_host, T0, _joiner);
        _repo.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.GetAsync(_host, lobby.Id);

        // Everyone is ready and the host is asking — the only reason start is absent is that M8 does not exist.
        // This is what makes the frontend's disabled button honest rather than decorative.
        result.Value!.AllowedActions.Should().NotContain(LobbyActions.Start);
        result.Value.AllowedActions.Should().Contain(LobbyActions.Invite);
        result.Value.DependencyReadiness.MatchRuntime.Should().BeFalse();
        result.Value.DependencyReadiness.Chat.Should().BeFalse();
        result.Value.DependencyReadiness.AiParticipants.Should().BeFalse();
    }

    [Fact]
    public async Task AllowedActions_DoNotOfferReadyToTheHost_WhoIsImplicitlyReady()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_joiner, T0);
        _repo.GetByIdAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var hostView = await _sut.GetAsync(_host, lobby.Id);
        var memberView = await _sut.GetAsync(_joiner, lobby.Id);

        hostView.Value!.AllowedActions.Should().NotContain(LobbyActions.Ready);
        memberView.Value!.AllowedActions.Should().Contain(LobbyActions.Ready);
        memberView.Value.AllowedActions.Should().NotContain(LobbyActions.Settings);
    }

    [Fact]
    public async Task Rematch_Returns503_BecauseModule8OwnsMatchRecords()
    {
        var result = await _sut.CreateRematchLobbyAsync(_host, Guid.NewGuid());

        result.Error!.Code.Should().Be(LobbyErrors.MatchRuntimeUnavailable);
        await _repo.DidNotReceive().AddLobbyAsync(
            Arg.Any<Lobby>(), Arg.Any<LobbyJoinCredential>(),
            Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    // ── Stale revision ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetReadiness_WithAStaleRevision_IsATypedConflictCarryingTheCurrentRevision()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_joiner, T0);   // bumps Revision to 2
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.SetReadinessAsync(
            _joiner, lobby.Id, new SetReadinessRequestDto(true, ExpectedRevision: 1));

        result.Error!.Code.Should().Be(LobbyErrors.StaleRevision);
        result.Error.Message.Should().Contain(lobby.Revision.ToString());
    }

    // ── Invites (R6) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptInvite_SeatsTheInvitee()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var invite = LobbyInvite.Create(lobby.Id, _host, _joiner, T0);
        _repo.GetInviteForUpdateAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.AcceptInviteAsync(_joiner, invite.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Seats.Should().HaveCount(2);
        invite.State.Should().Be(LobbyInviteState.Accepted);
    }

    /// <summary>An invite is permission to try, never a bypass of the lobby's own rules.</summary>
    [Fact]
    public async Task AcceptInvite_StillEnforcesBlocksAndTheOneActiveLobbyRule()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var invite = LobbyInvite.Create(lobby.Id, _host, _joiner, T0);
        _repo.GetInviteForUpdateAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _repo.GetBlockedCounterpartsAsync(_joiner, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { _host });

        var blockedResult = await _sut.AcceptInviteAsync(_joiner, invite.Id);
        blockedResult.Error!.Code.Should().Be(LobbyErrors.Blocked);

        _repo.GetBlockedCounterpartsAsync(_joiner, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());
        _repo.GetActiveTicketIdForUserAsync(_joiner, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());

        var freshInvite = LobbyInvite.Create(lobby.Id, _host, _joiner, T0);
        _repo.GetInviteForUpdateAsync(freshInvite.Id, Arg.Any<CancellationToken>()).Returns(freshInvite);

        var queuedResult = await _sut.AcceptInviteAsync(_joiner, freshInvite.Id);
        queuedResult.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
    }

    [Fact]
    public async Task AcceptInvite_AnotherUsersInviteId_IsAPrivacySafeNotFound()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var invite = LobbyInvite.Create(lobby.Id, _host, _joiner, T0);
        _repo.GetInviteForUpdateAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);

        var result = await _sut.AcceptInviteAsync(_stranger, invite.Id);

        result.Error!.Code.Should().Be(LobbyErrors.NotFound);
    }

    [Fact]
    public async Task AcceptInvite_AfterTheThirtyMinuteDeadline_IsExpired()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var invite = LobbyInvite.Create(lobby.Id, _host, _joiner, T0);
        _repo.GetInviteForUpdateAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        _clock.SetUtcNow(T0 + LobbyInvite.Lifetime);

        var result = await _sut.AcceptInviteAsync(_joiner, invite.Id);

        result.Error!.Code.Should().Be(LobbyErrors.Expired);
    }

    [Fact]
    public async Task CreateInvite_ToANonFriend_IsRefused_SoAPrivateLobbyCannotBeRevealed()
    {
        var lobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Private));
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _repo.AreFriendsAsync(_host, _stranger, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CreateInviteAsync(_host, lobby.Id, new CreateInviteRequestDto(_stranger));

        result.Error!.Code.Should().Be(LobbyErrors.InvalidTarget);
        await _repo.DidNotReceive().AddInviteAsync(
            Arg.Any<LobbyInvite>(), Arg.Any<IReadOnlyList<OutboxMessage>>(), Arg.Any<CancellationToken>());
    }

    // ── Credential rotation ──────────────────────────────────────────────────

    [Fact]
    public async Task RotateCredential_SupersedesTheOldValueAndBumpsTheGeneration()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        var outgoing = IssuedCredential(lobby.Id, "OLD-CODE");
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);
        _repo.GetActiveCredentialAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(outgoing);

        var result = await _sut.RotateCredentialAsync(_host, lobby.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Generation.Should().Be(2);
        result.Value.Code.Should().NotBe("OLD-CODE");

        // The old row is dead the instant this commits — there is no window in which both codes work.
        outgoing.State.Should().Be(LobbyCredentialState.Rotated);
        await _repo.Received(1).RotateCredentialAsync(
            Arg.Is<LobbyJoinCredential>(c => c.State == LobbyCredentialState.Rotated),
            Arg.Is<LobbyJoinCredential>(c => c.Generation == 2 && c.State == LobbyCredentialState.Active),
            Arg.Any<IReadOnlyList<OutboxMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RotateCredential_ByANonHostMember_IsForbidden()
    {
        var lobby = LobbyTestFactory.Open(_host, T0);
        lobby.Join(_joiner, T0);
        _repo.GetForUpdateAsync(lobby.Id, Arg.Any<CancellationToken>()).Returns(lobby);

        var result = await _sut.RotateCredentialAsync(_joiner, lobby.Id);

        result.Error!.Code.Should().Be(LobbyErrors.Forbidden);
    }

    // ── Capability profile read ──────────────────────────────────────────────

    [Fact]
    public async Task GetCapabilityProfile_ForAnActiveSlug_ReturnsThePinnedVersionSource()
    {
        _repo.GetActiveCapabilityProfileAsync("chess-lite", Arg.Any<CancellationToken>()).Returns(Profile());

        var result = await _sut.GetCapabilityProfileAsync("chess-lite");

        result.IsSuccess.Should().BeTrue();
        result.Value!.GameSlug.Should().Be("chess-lite");
        result.Value.CapabilityVersion.Should().Be(1);
        result.Value.AllowedModes.Should().Contain("multiplayer");
        result.Value.TimeControls.Should().Contain("blitz-3-2");
    }

    [Fact]
    public async Task GetCapabilityProfile_ForASlugWithNoActiveProfile_IsAPlain404_NotAPrivacyOracle()
    {
        // Unlike a lobby id, a game slug is public catalog data — there is nothing to leak by naming the code.
        _repo.GetActiveCapabilityProfileAsync("unknown-slug", Arg.Any<CancellationToken>())
            .Returns((GameCapabilityProfile?)null);

        var result = await _sut.GetCapabilityProfileAsync("unknown-slug");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(LobbyErrors.CapabilityNotFound);
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPublic_HidesFullExpiredAndBlockedLobbies_WithoutDisturbingTheCursor()
    {
        var openLobby = LobbyTestFactory.Open(_host, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));

        var fullHost = Guid.NewGuid();
        var fullLobby = LobbyTestFactory.Open(
            fullHost, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public, maxPlayers: 2));
        fullLobby.Join(Guid.NewGuid(), T0);

        var blockedHost = Guid.NewGuid();
        var blockedLobby = LobbyTestFactory.Open(
            blockedHost, T0, LobbyTestFactory.Settings(privacy: LobbyPrivacy.Public));

        _repo.GetPublicPageAsync(3, null, null, Arg.Any<CancellationToken>())
            .Returns(new[] { openLobby, fullLobby, blockedLobby });
        _repo.GetBlockedCounterpartsAsync(_stranger, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { blockedHost });

        var result = await _sut.GetPublicAsync(_stranger, limit: 3, cursor: null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle().Which.LobbyId.Should().Be(openLobby.Id);

        // The page is short, and that is correct. The cursor still advances past every row the *query* saw, so a
        // filtered-out trailing row can never wedge pagination in place.
        result.Value.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPublic_WithAForgedCursor_IsARejectedRequest_NotASilentRestart()
    {
        var result = await _sut.GetPublicAsync(_stranger, limit: 20, cursor: "not-a-real-cursor!!");

        result.Error!.Code.Should().Be(LobbyErrors.InvalidCursor);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static CreateLobbyRequestDto CreateRequest() => new(
        GameSlug: "chess-lite",
        CapabilityVersion: 1,
        Privacy: "Private",
        MaxPlayers: 4,
        TimeControlId: "blitz-3-2",
        Rated: false,
        Region: "eu-west",
        SpectatorPolicy: "Anyone",
        TieBreakRuleId: "none",
        AiFillRequested: false);

    private static GameCapabilityProfile Profile() => GameCapabilityProfile.Create(
        "chess-lite", 1, minPlayers: 2, maxPlayers: 4,
        allowedModes: new[] { "multiplayer", "cooperative" },
        timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
        tieBreakRules: new[] { "none", "sudden-death" },
        spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
        ratedEligible: false, aiFillEligible: false,
        manifestVersion: "2026.1");

    private static Game AvailableGame() => GameWithLifecycle(GameLifecycle.Available);

    private static Game GameWithLifecycle(GameLifecycle lifecycle) => Game.Create(
        slug: "chess-lite", name: "Chess Lite", summary: "A streamlined chess experience.",
        rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
        estimatedDurationMinMinutes: 10, estimatedDurationMaxMinutes: 20,
        minPlayers: 2, maxPlayers: 4, initialLifecycle: lifecycle,
        featuredRank: null, sortOrder: 1, artToken: "chess-lite",
        artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
        manifestVersion: "2026.1", category: "strategy",
        // Not "strategy" — M4's Game rejects a tag that duplicates the category.
        tags: new[] { "classic", "logic" },
        modes: new[] { "multiplayer", "cooperative" });

    private LobbyJoinCredential IssuedCredential(Guid lobbyId, string plaintextCode) =>
        LobbyJoinCredential.Issue(
            lobbyId, _hasher.HashCode(plaintextCode), _hasher.HashLinkToken(plaintextCode + "-link"), 1, T0);

    private static User MakeUser(Guid id)
    {
        var user = User.Create($"user{id:N}"[..12], $"{id:N}@example.com", "hash", "Test Player");
        typeof(SimPle.Domain.Common.Entity)
            .GetProperty(nameof(SimPle.Domain.Common.Entity.Id))!
            .SetValue(user, id);
        return user;
    }

    private static IReadOnlyDictionary<Guid, User> Roster(IReadOnlyList<Guid> ids) =>
        ids.ToDictionary(id => id, MakeUser);

    /// <summary>
    /// Runs the command exactly once. The real runner's retry, advisory lock, and transaction only mean anything
    /// against a database that enforces indexes and row versions, so they are proven against real PostgreSQL — a
    /// fake that "simulated" contention here would only prove the fake works.
    /// </summary>
    private sealed class PassThroughRunner : ILobbyCommandRunner
    {
        public Task<Result<T>> RunAsync<T>(
            Guid actorUserId, Func<CancellationToken, Task<Result<T>>> command, CancellationToken ct = default) =>
            command(ct);
    }
}
