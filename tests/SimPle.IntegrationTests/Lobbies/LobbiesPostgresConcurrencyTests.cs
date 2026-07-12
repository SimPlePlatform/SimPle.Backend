using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
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
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Outbox;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Lobbies;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using SimPle.Shared.Common;
using Xunit;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// Real-PostgreSQL tests for reconciliation <strong>R3</strong> — the bounded whole-command retry
/// (<see cref="LobbyCommandRunner"/>).
///
/// <para>
/// These cannot run on InMemory, and the reason is the entire point of the class. InMemory does not enforce
/// filtered unique indexes, CHECK constraints, or <c>xmin</c> row versions, so a last-seat-join race asserted
/// against it would pass whatever the code did — including code that overfilled the lobby or threw a 500. Every
/// assertion below is about behavior that only a real database can produce.
/// </para>
///
/// <para>
/// Each test builds the <em>real</em> <see cref="LobbiesService"/> over a real
/// <see cref="LobbyRepository"/> and a real <see cref="LobbyCommandRunner"/>, each on its own
/// <see cref="AppDbContext"/> — separate contexts are what make the racers genuinely concurrent rather than two
/// calls sharing one change tracker.
/// </para>
///
/// Skipped unless <c>MIGRATION_TEST_CONNECTION_STRING</c> points at a running PostgreSQL instance.
/// </summary>
public sealed class LobbiesPostgresConcurrencyTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_m6b_{Guid.NewGuid():N}";
    private string? _testConn;

    private static readonly DateTime T0 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        if (_masterConn is null) return;

        _testConn = new NpgsqlConnectionStringBuilder(_masterConn) { Database = _dbName }.ToString();

        await using var masterConn = new NpgsqlConnection(_masterConn);
        await masterConn.OpenAsync();
        await using var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
        await createCmd.ExecuteNonQueryAsync();

        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_masterConn is null || _testConn is null) return;

        NpgsqlConnection.ClearAllPools();

        await using var masterConn = new NpgsqlConnection(_masterConn);
        await masterConn.OpenAsync();

        await using var terminateCmd = masterConn.CreateCommand();
        terminateCmd.CommandText = $@"
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{_dbName}'
              AND pid <> pg_backend_pid()
              -- Only this role's own backends. Terminating another role's process raises 42501, and an
              -- autovacuum worker (which runs as the bootstrap superuser) can appear on this database at
              -- any moment -- so the unfiltered form fails intermittently under a least-privilege test role.
              -- The teardown below clears autovacuum on its own, so skipping those backends is safe.
              AND usename = current_user;";
        await terminateCmd.ExecuteNonQueryAsync();

        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\"";
        await dropCmd.ExecuteNonQueryAsync();
    }

    // ── The R3 test ──────────────────────────────────────────────────────────

    /// <summary>
    /// The reason <see cref="LobbyCommandRunner"/> exists.
    ///
    /// Two users race for the one remaining seat. Both read a lobby with room, both decide to join, both write.
    /// The lobby's <c>xmin</c> row version makes the loser's UPDATE affect zero rows — EF surfaces that as
    /// <c>DbUpdateConcurrencyException</c>, which is neither a unique violation nor a serialization failure, and
    /// which <c>PostgresRetry</c> would not catch at all.
    ///
    /// The runner reruns the <em>whole</em> command. The rerun re-reads, finds the lobby full, and answers a typed
    /// <c>Lobbies.Full</c>. Retrying only the <c>SaveChanges</c> — the pre-existing behavior this module
    /// deliberately does not reuse — would have replayed a decision made against stale state and either overfilled
    /// the lobby or surfaced a 500.
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentLastSeatJoins_ExactlyOneWins_AndTheLoserGetsATypedFull_NeverA500()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        var racerA = await SeedUserAsync();
        var racerB = await SeedUserAsync();
        await SeedCatalogAsync();

        // A 2-seat lobby with the host already seated: exactly one seat is left for two racers.
        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 2);
        var code = await SeedCredentialAsync(lobbyId, "RACE-CODE");

        await using var dbA = CreateDb();
        await using var dbB = CreateDb();

        var joinA = BuildService(dbA).JoinByCredentialAsync(racerA.Id, new JoinLobbyRequestDto(code, null));
        var joinB = BuildService(dbB).JoinByCredentialAsync(racerB.Id, new JoinLobbyRequestDto(code, null));

        var results = await Task.WhenAll(joinA, joinB);

        // Exactly one winner.
        results.Count(r => r.IsSuccess).Should().Be(1);

        // The loser is a typed, honest conflict — not an exception, not a 500, not a silent overfill.
        var loser = results.Single(r => !r.IsSuccess);
        loser.Error!.Code.Should().Be(LobbyErrors.Full);

        // And the database agrees: two seats, no more.
        await using var verify = CreateDb();
        var seated = await verify.LobbyMembers
            .CountAsync(m => m.LobbyId == lobbyId && m.State == LobbyMemberState.Joined);
        seated.Should().Be(2);
    }

    /// <summary>
    /// Proves the retry <em>mechanism</em> fires, deterministically — the test above proves the observable
    /// outcome, but two tasks racing on a fast machine can serialize by luck, in which case the loser would read a
    /// full lobby and answer <c>Lobbies.Full</c> without the runner ever retrying anything. That test would then
    /// pass with <see cref="LobbyCommandRunner"/>'s retry as dead code. This one cannot.
    ///
    /// <para>
    /// The setup forces the exact failure: context A loads the lobby, then a <em>different</em> context commits a
    /// change to it. A's tracked copy is now stale. EF returns the <em>tracked</em> instance from A's next read
    /// (that is what a change tracker does), so the first attempt writes against a stale <c>xmin</c> and the
    /// UPDATE affects zero rows — <c>DbUpdateConcurrencyException</c>.
    /// </para>
    ///
    /// <para>
    /// The runner must then clear A's change tracker and rerun the whole delegate, so the second attempt genuinely
    /// re-reads. Asserting the delegate ran <strong>twice</strong> is what proves both halves: the retry, and the
    /// <c>ChangeTracker.Clear()</c> without which the rerun would re-read the same stale entity and lose forever.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task CommandRunner_OnAStaleRowVersion_ClearsTheTrackerAndRerunsTheWholeCommand()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        var joiner = await SeedUserAsync();
        await SeedCatalogAsync();

        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);

        await using var dbA = CreateDb();
        var repoA = new LobbyRepository(dbA);
        var runner = new LobbyCommandRunner(dbA, NullLogger<LobbyCommandRunner>.Instance);

        // A loads the lobby and holds it tracked.
        var staleLobby = await repoA.GetForUpdateAsync(lobbyId);
        staleLobby!.Revision.Should().Be(1);

        // A different connection seats a member, bumping Revision (and xmin) underneath A. A's tracked copy is now
        // stale, and it does not know it.
        await using (var dbB = CreateDb())
        {
            var repoB = new LobbyRepository(dbB);
            var lobbyB = await repoB.GetForUpdateAsync(lobbyId);
            lobbyB!.Join(joiner.Id, T0).Should().Be(LobbyOutcome.Ok);
            await repoB.SaveAsync(Array.Empty<OutboxMessage>());
        }

        var attempts = 0;

        var result = await runner.RunAsync<int>(host.Id, async ct =>
        {
            attempts++;

            // Re-read through the repository. On attempt 1 the change tracker still holds the stale instance and
            // EF hands that back; on attempt 2 the tracker has been cleared, so this is a genuine fresh read.
            var lobby = await repoA.GetForUpdateAsync(lobbyId, ct);
            lobby!.ChangeSettings(
                host.Id,
                lobby.CurrentSettings with { TimeControlId = "rapid-10-0" },
                T0);

            await repoA.SaveAsync(Array.Empty<OutboxMessage>(), ct);
            return Result<int>.Ok(lobby.Revision);
        });

        result.IsSuccess.Should().BeTrue();

        // The whole read-decide-write ran twice: once against the stale row (which lost), once against the fresh
        // one (which won). Exactly the behavior PostgresRetry's save-only retry could not have produced.
        attempts.Should().Be(2);

        await using var verify = CreateDb();
        var final = await verify.Lobbies.AsNoTracking().SingleAsync(l => l.Id == lobbyId);
        final.TimeControlId.Should().Be("rapid-10-0");
    }

    /// <summary>
    /// The cross-table half of the one-active-lobby-<strong>or</strong>-ticket invariant (brief Risk #2).
    ///
    /// Two filtered unique indexes on different tables cannot see each other. Under READ COMMITTED, a join and an
    /// enqueue racing for the same user would each read "nothing active" — neither seeing the other's uncommitted
    /// row — and both would commit, leaving the user in a lobby <em>and</em> a queue.
    ///
    /// The runner's transaction-scoped <c>pg_advisory_xact_lock</c> on the actor is what closes that window. Here
    /// the ticket is committed first, so the join must see it and refuse.
    /// </summary>
    [SkippableFact]
    public async Task Join_WhileHoldingAQueuedTicket_IsRefused_AcrossTables()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        var joiner = await SeedUserAsync();
        await SeedCatalogAsync();

        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);
        var code = await SeedCredentialAsync(lobbyId, "TICKET-CODE");

        await using (var seedDb = CreateDb())
        {
            seedDb.MatchmakingTickets.Add(MatchmakingTicket.Enqueue(
                joiner.Id, "chess-lite", 1, "multiplayer", 2, "blitz-3-2", false, "eu-west",
                MatchmakingTicket.ProvisionalRating, MatchmakingTicket.ProvisionalRatingSource,
                Guid.NewGuid(), T0));
            await seedDb.SaveChangesAsync();
        }

        await using var db = CreateDb();
        var result = await BuildService(db).JoinByCredentialAsync(joiner.Id, new JoinLobbyRequestDto(code, null));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);

        await using var verify = CreateDb();
        (await verify.LobbyMembers.AnyAsync(m => m.UserId == joiner.Id && m.State == LobbyMemberState.Joined))
            .Should().BeFalse();
    }

    /// <summary>
    /// The same user double-submitting a create (a double-clicked button, a retried request). The advisory lock
    /// serializes them; the second sees the first's committed lobby and refuses. Without the lock, both would read
    /// "no active lobby" and the <c>ux_lobby_members_one_joined_per_user</c> index would catch it as a raw 23505 —
    /// correct, but only after the second lobby row had already been written and had to be rolled back.
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentCreatesByTheSameUser_ProduceExactlyOneLobby()
    {
        SkipIfNoPg();

        var user = await SeedUserAsync();
        await SeedCatalogAsync();

        await using var dbA = CreateDb();
        await using var dbB = CreateDb();

        var createA = BuildService(dbA).CreateAsync(user.Id, CreateRequest());
        var createB = BuildService(dbB).CreateAsync(user.Id, CreateRequest());

        var results = await Task.WhenAll(createA, createB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => !r.IsSuccess).Error!.Code.Should().Be(LobbyErrors.AlreadyActive);

        await using var verify = CreateDb();
        (await verify.Lobbies.CountAsync(l => l.HostUserId == user.Id)).Should().Be(1);
        (await verify.LobbyMembers.CountAsync(m => m.UserId == user.Id && m.State == LobbyMemberState.Joined))
            .Should().Be(1);
    }

    /// <summary>
    /// The join code's uniqueness index is real, and a redeemed credential is durable: the digest — never the
    /// plaintext — is what is stored, and the plaintext cannot be recovered from the row.
    /// </summary>
    [SkippableFact]
    public async Task AJoinCredential_IsPersistedOnlyAsADigest()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        await SeedCatalogAsync();

        await using var db = CreateDb();
        var result = await BuildService(db).CreateAsync(host.Id, CreateRequest());
        result.IsSuccess.Should().BeTrue();

        var plaintextCode = result.Value!.Credential.Code;
        var plaintextToken = result.Value.Credential.LinkToken;

        await using var verify = CreateDb();
        var credential = await verify.LobbyJoinCredentials
            .SingleAsync(c => c.LobbyId == result.Value.Lobby.LobbyId);

        credential.CodeDigest.Should().NotBe(plaintextCode);
        credential.LinkTokenDigest.Should().NotBe(plaintextToken);
        credential.CodeDigest.Should().NotContain(plaintextCode);

        // The two secrets are independent — a leaked code must not imply the link token.
        credential.CodeDigest.Should().NotBe(credential.LinkTokenDigest);
    }

    /// <summary>
    /// Outbox atomicity: the membership change and its integration event commit together. A joined member with no
    /// <c>LobbyMemberJoinedV1</c> row would leave M7/M11 permanently unaware that someone is in the lobby.
    /// </summary>
    [SkippableFact]
    public async Task AJoin_CommitsItsOutboxEventInTheSameTransactionAsTheSeat()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        var joiner = await SeedUserAsync();
        await SeedCatalogAsync();

        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);
        var code = await SeedCredentialAsync(lobbyId, "OUTBOX-CODE");

        await using var db = CreateDb();
        var result = await BuildService(db).JoinByCredentialAsync(joiner.Id, new JoinLobbyRequestDto(code, null));
        result.IsSuccess.Should().BeTrue();

        await using var verify = CreateDb();
        var seated = await verify.LobbyMembers
            .AnyAsync(m => m.LobbyId == lobbyId && m.UserId == joiner.Id && m.State == LobbyMemberState.Joined);
        var evented = await verify.OutboxMessages
            .AnyAsync(e => e.AggregateId == lobbyId && e.EventType == "LobbyMemberJoinedV1");

        seated.Should().BeTrue();
        evented.Should().BeTrue();

        // No credential ever reaches an outbox payload — it is durable, replayable, and read by every future
        // consumer, so a secret that landed in one would be permanently disclosed.
        var payloads = await verify.OutboxMessages
            .Where(e => e.AggregateId == lobbyId)
            .Select(e => e.Payload)
            .ToListAsync();
        payloads.Should().NotBeEmpty();
        payloads.Should().OnlyContain(p => !p.Contains(code));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private void SkipIfNoPg() => Skip.If(_masterConn is null,
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run Module 6B race tests.");

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private static CreateLobbyRequestDto CreateRequest() => new(
        GameSlug: "chess-lite", CapabilityVersion: 1, Privacy: "Private", MaxPlayers: 4,
        TimeControlId: "blitz-3-2", Rated: false, Region: "eu-west",
        SpectatorPolicy: "Anyone", TieBreakRuleId: "none", AiFillRequested: false);

    /// <summary>
    /// The real service over the real repository and the real command runner, on the supplied context. Only the
    /// leaf collaborators M6 does not own (storage, the M5 registry, the M7/M8/M9 probes) are substituted.
    /// </summary>
    private LobbiesService BuildService(AppDbContext db)
    {
        var hasher = new HmacLobbyCredentialHasher(
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }));

        var throttle = Substitute.For<ILobbyJoinThrottle>();
        throttle.GetRetryAfterUtcAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DateTime?)null);

        var matchRuntime = Substitute.For<IMatchRuntimeProbe>();
        matchRuntime.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        matchRuntime.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var chat = Substitute.For<IChatRuntimeProbe>();
        chat.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var ai = Substitute.For<IAiParticipantProbe>();
        ai.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        var engines = Substitute.For<IGameRegistry>();
        engines.RegisteredDefinitions.Returns(Array.Empty<GameDefinitionMetadata>());

        var storage = Substitute.For<IFileStorageService>();

        return new LobbiesService(
            new LobbyRepository(db),
            new LobbyCommandRunner(db, NullLogger<LobbyCommandRunner>.Instance),
            hasher, throttle, matchRuntime, chat, ai, engines,
            new UserRepository(db), storage,
            Options.Create(new StorageOptions()),
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }),
            new FakeTimeProvider(T0),
            NullLogger<LobbiesService>.Instance);
    }

    private const string TestKey = "postgres-race-tests-lobby-credential-key";

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateDb();
        var g = Guid.NewGuid();
        var user = User.Create($"m6b{g:N}"[..20], $"m6b{g:N}@test.io", "hash", "M6B Race User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task SeedCatalogAsync()
    {
        await using var db = CreateDb();

        db.Games.Add(Game.Create(
            slug: "chess-lite", name: "Chess Lite", summary: "A streamlined chess experience.",
            rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
            estimatedDurationMinMinutes: 10, estimatedDurationMaxMinutes: 20,
            minPlayers: 2, maxPlayers: 4, initialLifecycle: GameLifecycle.Available,
            featuredRank: null, sortOrder: 1, artToken: "chess-lite",
            artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
            manifestVersion: "2026.1", category: "strategy",
            tags: new[] { "classic" },
            modes: new[] { "multiplayer", "cooperative" }));

        db.GameCapabilityProfiles.Add(GameCapabilityProfile.Create(
            "chess-lite", 1, minPlayers: 2, maxPlayers: 4,
            allowedModes: new[] { "multiplayer", "cooperative" },
            timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
            tieBreakRules: new[] { "none", "sudden-death" },
            spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
            ratedEligible: false, aiFillEligible: false,
            manifestVersion: "2026.1"));

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedLobbyAsync(Guid hostId, int maxPlayers)
    {
        await using var db = CreateDb();
        var lobby = Lobby.Create(
            hostId,
            new LobbySettings("chess-lite", 1, LobbyPrivacy.Private, maxPlayers, "blitz-3-2", false,
                "eu-west", SpectatorPolicy.Anyone, "none", false),
            Guid.NewGuid(), T0);
        db.Lobbies.Add(lobby);
        await db.SaveChangesAsync();
        return lobby.Id;
    }

    private async Task<string> SeedCredentialAsync(Guid lobbyId, string plaintextCode)
    {
        var hasher = new HmacLobbyCredentialHasher(
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }));

        await using var db = CreateDb();
        db.LobbyJoinCredentials.Add(LobbyJoinCredential.Issue(
            lobbyId, hasher.HashCode(plaintextCode), hasher.HashLinkToken(plaintextCode + "-link"), 1, T0));
        await db.SaveChangesAsync();

        return plaintextCode;
    }
}
