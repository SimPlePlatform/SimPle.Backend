using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// PostgreSQL-only tests for the AddLobbyMatchmakingAndCapabilities migration. Skipped unless
/// MIGRATION_TEST_CONNECTION_STRING points at a running PostgreSQL instance. Each test METHOD gets its own
/// isolated database (xUnit IAsyncLifetime), created in InitializeAsync and dropped in DisposeAsync.
///
/// These tests exist because the application layer CANNOT enforce Module 6's core invariants. A C# "is this user
/// already in a lobby?" check reads, decides, and writes across a window in which another transaction can do the
/// same — both see "no", both insert, and the invariant is gone. Only a partial unique index rejects the second
/// writer. Everything asserted here is therefore asserted against a real database, never InMemory (which silently
/// ignores filtered indexes and CHECK constraints entirely).
/// </summary>
public sealed class LobbyMatchmakingMigrationTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_m6_smoke_{Guid.NewGuid():N}";
    private string? _testConn;

    public async Task InitializeAsync()
    {
        if (_masterConn is null) return;

        var builder = new NpgsqlConnectionStringBuilder(_masterConn) { Database = _dbName };
        _testConn = builder.ToString();

        await using var masterConn = new NpgsqlConnection(_masterConn);
        await masterConn.OpenAsync();
        await using var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
        await createCmd.ExecuteNonQueryAsync();

        await using var db = CreateTestDb();
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

    private void SkipIfNoPg() => Skip.If(_masterConn is null,
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run Module 6 smoke tests.");

    private AppDbContext CreateTestDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private static readonly DateTime T0 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateTestDb();
        var g = Guid.NewGuid();
        var user = User.Create($"m6sm{g:N}"[..24], $"m6sm{g:N}@test.io", "hash", "M6 Smoke User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Game> SeedGameAsync(string slug, int minPlayers = 2, int maxPlayers = 4,
        params string[] modes)
    {
        await using var db = CreateTestDb();
        var game = Game.Create(
            slug, "Test Game", "Summary.", "Rules.", GameDifficulty.Easy,
            1, 5, minPlayers, maxPlayers, GameLifecycle.ComingSoon, null, 0,
            "art-token", "#111111", "#222222", "Test artwork", "2026.1",
            "strategy", new[] { "puzzle" },
            modes.Length > 0 ? modes : new[] { "multiplayer", "ranked", "ai" });
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game;
    }

    private static LobbySettings Settings(string gameSlug = "smoke-game", int maxPlayers = 4) =>
        new(gameSlug, 1, LobbyPrivacy.Private, maxPlayers, "blitz-3-2", false,
            "eu-west", SpectatorPolicy.Anyone, "none", false);

    private static async Task<Lobby> AddLobbyAsync(AppDbContext db, Guid hostId, string gameSlug, int maxPlayers = 4)
    {
        var lobby = Lobby.Create(hostId, Settings(gameSlug, maxPlayers), Guid.NewGuid(), T0);
        db.Lobbies.Add(lobby);
        await db.SaveChangesAsync();
        return lobby;
    }

    private static bool IsUniqueViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } };

    private static bool IsCheckViolation(Exception ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: "23514" } };

    // ── Migration health ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_CreatesEveryModule6Table()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var table in new[]
                 {
                     "lobbies", "lobby_members", "lobby_invites", "lobby_join_credentials",
                     "lobby_start_requests", "matchmaking_tickets", "matchmaking_assignments",
                     "game_capability_profiles", "capability_seed_history",
                 })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task Migration_CreatesEveryPartialUniqueIndex_AsActuallyPartial()
    {
        SkipIfNoPg();

        // Asserting the index exists is not enough: an index created WITHOUT its filter would still be "present"
        // while silently forbidding a user from ever re-joining a lobby they had left. The WHERE clause is the
        // whole point, so it is asserted explicitly.
        var expected = new (string Index, string MustContain)[]
        {
            ("ux_lobby_members_one_joined_per_user", "Joined"),
            ("ux_matchmaking_tickets_one_nonterminal_per_user", "Queued"),
            ("ux_matchmaking_assignments_one_active_per_ticket", "Active"),
            ("ux_lobby_join_credentials_active_code", "Active"),
            ("ux_lobby_start_requests_one_open_per_revision", "Open"),
            ("ux_game_capability_profiles_one_active_per_game", "IsActive"),
            ("ix_lobbies_public_discovery", "Public"),
        };

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var (index, mustContain) in expected)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = @i";
            cmd.Parameters.AddWithValue("i", index);

            var indexDef = (string?)await cmd.ExecuteScalarAsync();

            Assert.NotNull(indexDef);
            Assert.Contains("WHERE", indexDef!);
            Assert.Contains(mustContain, indexDef!);
        }
    }

    // ── One joined membership per user, ACROSS lobbies ───────────────────────

    [SkippableFact]
    public async Task OneJoinedMembershipPerUser_IsEnforcedAcrossDifferentLobbies()
    {
        SkipIfNoPg();

        // The cross-lobby case is the one an application check reliably gets wrong, because the natural query is
        // scoped to the lobby being joined.
        var host = await SeedUserAsync();
        var joiner = await SeedUserAsync();
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobbyA = await AddLobbyAsync(db, host.Id, "smoke-game");
        var lobbyB = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        lobbyA.Join(joiner.Id, T0);
        await db.SaveChangesAsync();

        lobbyB.Join(joiner.Id, T0);

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex), "a second joined seat in another lobby must be rejected by the index");
    }

    [SkippableFact]
    public async Task AUserWhoLeftALobbyMayJoinAnother()
    {
        SkipIfNoPg();

        // The mirror image: the filter must be narrow enough that departing frees the user. If the index were not
        // partial, a user could join exactly one lobby ever.
        var joiner = await SeedUserAsync();
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobbyA = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");
        var lobbyB = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        lobbyA.Join(joiner.Id, T0);
        await db.SaveChangesAsync();

        lobbyA.Leave(joiner.Id, T0);
        await db.SaveChangesAsync();

        lobbyB.Join(joiner.Id, T0);
        await db.SaveChangesAsync();   // must not throw

        var joinedCount = await db.LobbyMembers
            .CountAsync(m => m.UserId == joiner.Id && m.State == LobbyMemberState.Joined);
        Assert.Equal(1, joinedCount);
    }

    [SkippableFact]
    public async Task ConcurrentLastSeatJoins_ProduceExactlyOneWinner_AndNeverOverfillTheLobby()
    {
        SkipIfNoPg();

        // Two genuinely concurrent writers, each on its own connection, racing for the final seat.
        //
        // Capacity is the one Module 6 invariant a partial unique index CANNOT express: "joined members <
        // MaxPlayers" is a COUNT, not a uniqueness property, and the member index is keyed on UserId — so it
        // happily admits two *different* users into the same last seat. What actually serializes them is the
        // lobby's xmin row version: every join bumps Revision, so both racers issue
        //     UPDATE lobbies SET "Revision" = 2, ... WHERE "Id" = @id AND xmin = @loaded
        // and only the first can match. The loser's UPDATE affects 0 rows and surfaces as a concurrency conflict —
        // exactly the signal 6B's BoundedTransactionRetry (R3) reruns on, re-reading the lobby to discover it is
        // now full and returning a typed Lobbies.Full rather than a 500.
        //
        // This test pins that mechanism. Remove the Revision bump or the row version and the lobby would silently
        // overfill; nothing else in the suite would notice.
        var host = await SeedUserAsync();
        var alice = await SeedUserAsync();
        var bob = await SeedUserAsync();
        await SeedGameAsync("smoke-game");

        Guid lobbyId;
        await using (var setup = CreateTestDb())
        {
            var lobby = await AddLobbyAsync(setup, host.Id, "smoke-game", maxPlayers: 2);
            lobbyId = lobby.Id;
        }

        using var bothHaveRead = new Barrier(2);

        async Task<bool> TryJoinAsync(Guid userId)
        {
            await using var db = CreateTestDb();
            var lobby = await db.Lobbies.Include(l => l.Members).FirstAsync(l => l.Id == lobbyId);

            // Both racers have now READ a lobby with one free seat. Releasing them together is what makes the race
            // real rather than incidentally serialized by the scheduler.
            bothHaveRead.SignalAndWait();

            if (lobby.Join(userId, T0) != LobbyOutcome.Ok) return false;

            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;   // lost the row-version race
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return false;   // lost the member-index race
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => TryJoinAsync(alice.Id)),
            Task.Run(() => TryJoinAsync(bob.Id)));

        Assert.Equal(1, results.Count(won => won));

        await using var verify = CreateTestDb();

        var seated = await verify.LobbyMembers
            .CountAsync(m => m.LobbyId == lobbyId && m.State == LobbyMemberState.Joined);
        Assert.Equal(2, seated);   // the host plus exactly one racer — never 3 in a 2-seat lobby

        var storedLobby = await verify.Lobbies.AsNoTracking().FirstAsync(l => l.Id == lobbyId);
        Assert.Equal(2, storedLobby.Revision);   // exactly one join applied => exactly one revision bump
    }

    // ── One nonterminal ticket per user ──────────────────────────────────────

    [SkippableFact]
    public async Task OneNonterminalTicketPerUser_IsEnforced()
    {
        SkipIfNoPg();

        var user = await SeedUserAsync();

        await using var db = CreateTestDb();
        db.MatchmakingTickets.Add(NewTicket(user.Id));
        await db.SaveChangesAsync();

        db.MatchmakingTickets.Add(NewTicket(user.Id));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex));
    }

    [SkippableFact]
    public async Task ACancelledTicketFreesTheUserToQueueAgain()
    {
        SkipIfNoPg();

        var user = await SeedUserAsync();

        await using var db = CreateTestDb();
        var first = NewTicket(user.Id);
        db.MatchmakingTickets.Add(first);
        await db.SaveChangesAsync();

        first.Cancel(T0);
        await db.SaveChangesAsync();

        db.MatchmakingTickets.Add(NewTicket(user.Id));
        await db.SaveChangesAsync();   // must not throw
    }

    // ── Zero duplicate assignment: the Risk #1 boundary ──────────────────────

    [SkippableFact]
    public async Task TwoCompetingWorkers_ProduceZeroDuplicateAssignments()
    {
        SkipIfNoPg();

        // This is the assertion the brief singles out. SKIP LOCKED prevents two workers CONTENDING on one row, but
        // it is not exclusivity: a requeued ticket or a serialization retry can still attempt a second assignment.
        // Only ux_matchmaking_assignments_one_active_per_ticket makes double-assignment impossible — so the test
        // deliberately bypasses any row lock and has both "workers" go straight for the insert.
        var user = await SeedUserAsync();

        Guid ticketId;
        await using (var setup = CreateTestDb())
        {
            var ticket = NewTicket(user.Id);
            setup.MatchmakingTickets.Add(ticket);
            await setup.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        async Task<bool> TryAssignAsync()
        {
            await using var db = CreateTestDb();
            db.MatchmakingAssignments.Add(
                MatchmakingAssignment.Create(ticketId, Guid.NewGuid(), Guid.NewGuid(), T0));
            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryAssignAsync(), TryAssignAsync(), TryAssignAsync());

        Assert.Equal(1, results.Count(won => won));

        await using var verify = CreateTestDb();
        var active = await verify.MatchmakingAssignments
            .CountAsync(a => a.TicketId == ticketId && a.State == MatchmakingAssignmentState.Active);
        Assert.Equal(1, active);
    }

    [SkippableFact]
    public async Task ASupersededAssignmentAllowsTheTicketToBeAssignedAgain()
    {
        SkipIfNoPg();

        // The legitimate re-assignment path after a failed handoff. If the index were not partial, a requeued
        // ticket could never be matched again.
        var user = await SeedUserAsync();

        await using var db = CreateTestDb();
        var ticket = NewTicket(user.Id);
        db.MatchmakingTickets.Add(ticket);
        await db.SaveChangesAsync();

        var first = MatchmakingAssignment.Create(ticket.Id, Guid.NewGuid(), Guid.NewGuid(), T0);
        db.MatchmakingAssignments.Add(first);
        await db.SaveChangesAsync();

        first.Supersede(T0);
        await db.SaveChangesAsync();

        db.MatchmakingAssignments.Add(
            MatchmakingAssignment.Create(ticket.Id, Guid.NewGuid(), Guid.NewGuid(), T0));
        await db.SaveChangesAsync();   // must not throw
    }

    // ── Credential uniqueness and rotation ───────────────────────────────────

    [SkippableFact]
    public async Task TwoActiveCredentialsCannotShareACodeDigest()
    {
        SkipIfNoPg();

        // Join-by-code looks the digest up on its own, so two live lobbies sharing a code would make the lookup
        // ambiguous. The generator's bounded collision retry catches exactly this 23505.
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobbyA = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");
        var lobbyB = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        db.LobbyJoinCredentials.Add(
            LobbyJoinCredential.Issue(lobbyA.Id, "same-code-digest", "link-a", 1, T0));
        await db.SaveChangesAsync();

        db.LobbyJoinCredentials.Add(
            LobbyJoinCredential.Issue(lobbyB.Id, "same-code-digest", "link-b", 1, T0));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex));
    }

    [SkippableFact]
    public async Task RotationSupersedesTheOldCredentialAndAllowsANewActiveOne()
    {
        SkipIfNoPg();

        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobby = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        var gen1 = LobbyJoinCredential.Issue(lobby.Id, "digest-gen-1", "link-gen-1", 1, T0);
        db.LobbyJoinCredentials.Add(gen1);
        await db.SaveChangesAsync();

        // A second ACTIVE credential for the same lobby must be impossible while gen1 is still active.
        db.LobbyJoinCredentials.Add(LobbyJoinCredential.Issue(lobby.Id, "digest-gen-2", "link-gen-2", 2, T0));
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex), "one active credential per lobby");

        // Rotate properly: supersede, then issue.
        await using var db2 = CreateTestDb();
        var stored = await db2.LobbyJoinCredentials.FirstAsync(c => c.LobbyId == lobby.Id);
        stored.MarkRotated(T0);
        db2.LobbyJoinCredentials.Add(LobbyJoinCredential.Issue(lobby.Id, "digest-gen-2", "link-gen-2", 2, T0));
        await db2.SaveChangesAsync();   // must not throw

        var active = await db2.LobbyJoinCredentials
            .CountAsync(c => c.LobbyId == lobby.Id && c.State == LobbyCredentialState.Active);
        Assert.Equal(1, active);
    }

    // ── One open start-request per lobby revision ────────────────────────────

    [SkippableFact]
    public async Task OneOpenStartRequestPerLobbyRevision_IsEnforced()
    {
        SkipIfNoPg();

        // This is what makes a retried Start idempotent rather than a second match request.
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobby = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        db.LobbyStartRequests.Add(
            LobbyStartRequest.Open(lobby.Id, 3, Guid.NewGuid(), "idem-a", Guid.NewGuid()));
        await db.SaveChangesAsync();

        db.LobbyStartRequests.Add(
            LobbyStartRequest.Open(lobby.Id, 3, Guid.NewGuid(), "idem-b", Guid.NewGuid()));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex));
    }

    [SkippableFact]
    public async Task AFailedStartRequestAllowsARetryAtANewRevision()
    {
        SkipIfNoPg();

        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobby = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        var first = LobbyStartRequest.Open(lobby.Id, 3, Guid.NewGuid(), "idem-a", Guid.NewGuid());
        db.LobbyStartRequests.Add(first);
        await db.SaveChangesAsync();

        first.MarkFailed("Match runtime unavailable.", T0);
        await db.SaveChangesAsync();

        db.LobbyStartRequests.Add(
            LobbyStartRequest.Open(lobby.Id, 4, Guid.NewGuid(), "idem-b", Guid.NewGuid()));
        await db.SaveChangesAsync();   // must not throw
    }

    // ── CHECK constraints ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AQueuedTicketCannotNameAWorker()
    {
        SkipIfNoPg();

        // A stale worker id on a queued row would mean a claim leaked and two workers could believe they hold it.
        var user = await SeedUserAsync();

        await using var db = CreateTestDb();
        var ticket = NewTicket(user.Id);
        db.MatchmakingTickets.Add(ticket);
        await db.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE matchmaking_tickets SET \"ClaimedByWorker\" = 'ghost' WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("id", ticket.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [SkippableFact]
    public async Task ATerminalTicketMayKeepItsWorkerAttribution()
    {
        SkipIfNoPg();

        // The mirror of the above: the constraint must NOT be so strict that it throws away the attribution behind
        // the matchmaking-worker-failure observability signal.
        var user = await SeedUserAsync();

        await using var db = CreateTestDb();
        var ticket = NewTicket(user.Id);
        db.MatchmakingTickets.Add(ticket);
        await db.SaveChangesAsync();

        ticket.Claim("worker-1", T0);
        ticket.MarkMatched(T0);
        await db.SaveChangesAsync();   // must not throw

        var stored = await db.MatchmakingTickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.Equal("worker-1", stored.ClaimedByWorker);
        Assert.Equal(MatchmakingTicketState.Matched, stored.State);
    }

    [SkippableFact]
    public async Task ATerminalLobbyMustCarryAClosedReason()
    {
        SkipIfNoPg();

        // An unexplained Closed row is a bug, not a valid state — the reason is the audit trail for why a lobby
        // stopped existing.
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobby = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE lobbies SET \"State\" = 'Closed' WHERE \"Id\" = @id";   // no ClosedReason
        cmd.Parameters.AddWithValue("id", lobby.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [SkippableFact]
    public async Task ALiveLobbyMustNotCarryAClosedReason()
    {
        SkipIfNoPg();

        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var lobby = await AddLobbyAsync(db, (await SeedUserAsync()).Id, "smoke-game");

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE lobbies SET \"ClosedReason\" = 'HostLeft' WHERE \"Id\" = @id";   // still Open
        cmd.Parameters.AddWithValue("id", lobby.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    // ── Capability profiles (D2) ─────────────────────────────────────────────

    [SkippableFact]
    public async Task ACapabilityProfileRequiresARealCatalogGame()
    {
        SkipIfNoPg();

        // The FK to games.slug. A profile pointing at a game that does not exist would resolve to nothing at
        // command time, so it must not be storable at all.
        await using var db = CreateTestDb();
        db.GameCapabilityProfiles.Add(NewProfile("does-not-exist"));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", ((PostgresException)ex.InnerException!).SqlState);   // foreign_key_violation
    }

    [SkippableFact]
    public async Task OnlyOneCapabilityProfilePerGameMayBeActive()
    {
        SkipIfNoPg();

        // Otherwise a create command would have to choose between two live profiles for the same game.
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        db.GameCapabilityProfiles.Add(NewProfile("smoke-game", capabilityVersion: 1));
        await db.SaveChangesAsync();

        db.GameCapabilityProfiles.Add(NewProfile("smoke-game", capabilityVersion: 2));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex));
    }

    [SkippableFact]
    public async Task ADeactivatedProfileAllowsANewActiveVersionToBePublished()
    {
        SkipIfNoPg();

        // The version-bump path: publish v2 by deactivating v1, never by editing v1 — a lobby that pinned v1 must
        // keep meaning what it meant when it was created.
        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var v1 = NewProfile("smoke-game", capabilityVersion: 1);
        db.GameCapabilityProfiles.Add(v1);
        await db.SaveChangesAsync();

        v1.Deactivate();
        db.GameCapabilityProfiles.Add(NewProfile("smoke-game", capabilityVersion: 2));
        await db.SaveChangesAsync();   // must not throw

        var pinnedV1 = await db.GameCapabilityProfiles.AsNoTracking()
            .FirstAsync(p => p.GameSlug == "smoke-game" && p.CapabilityVersion == 1);
        Assert.False(pinnedV1.IsActive);
        Assert.Equal(2, await db.GameCapabilityProfiles.CountAsync(p => p.GameSlug == "smoke-game"));
    }

    [SkippableFact]
    public async Task TheSamePinCannotBePublishedTwice()
    {
        SkipIfNoPg();

        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        var v1 = NewProfile("smoke-game", capabilityVersion: 1);
        db.GameCapabilityProfiles.Add(v1);
        await db.SaveChangesAsync();

        v1.Deactivate();
        await db.SaveChangesAsync();

        // Even deactivated, (slug, version) is the immutable pin — a second v1 would make the pin ambiguous.
        db.GameCapabilityProfiles.Add(NewProfile("smoke-game", capabilityVersion: 1));

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsUniqueViolation(ex));
    }

    [SkippableFact]
    public async Task CapabilityListsRoundTripThroughPostgresTextArrays()
    {
        SkipIfNoPg();

        await SeedGameAsync("smoke-game");

        await using var db = CreateTestDb();
        db.GameCapabilityProfiles.Add(NewProfile("smoke-game"));
        await db.SaveChangesAsync();

        await using var read = CreateTestDb();
        var stored = await read.GameCapabilityProfiles.AsNoTracking().FirstAsync();

        Assert.Equal(new[] { "multiplayer", "ranked", "ai" }, stored.AllowedModes);
        Assert.Equal(new[] { "blitz-3-2", "rapid-10-0" }, stored.TimeControls);
        Assert.Equal(new[] { "none", "sudden-death" }, stored.TieBreakRules);
        Assert.Equal(new[] { "Anyone", "FriendsOnly", "Disabled" }, stored.SpectatorPolicies);
    }

    // ── Module 4's tables are untouched ──────────────────────────────────────

    [SkippableFact]
    public async Task Module4AndModule3TablesAreUnchangedByThisMigration()
    {
        SkipIfNoPg();

        // The migration is additive. The one thing it does add to an M4 table is the AK_games_Slug unique
        // constraint, which the FK from game_capability_profiles requires — it drops nothing and cannot fail on
        // existing data, since IX_games_Slug already guaranteed uniqueness.
        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        await using var akCmd = conn.CreateCommand();
        akCmd.CommandText = @"
            SELECT COUNT(*) FROM pg_constraint
            WHERE conname = 'AK_games_Slug' AND contype = 'u';";
        Assert.Equal(1L, (long)(await akCmd.ExecuteScalarAsync())!);

        // No M3/M4 column was dropped or retyped: spot-check the columns Module 6 reads.
        await using var colCmd = conn.CreateCommand();
        colCmd.CommandText = @"
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = 'games' AND column_name IN ('Slug', 'MinPlayers', 'MaxPlayers', 'Lifecycle');";
        Assert.Equal(4L, (long)(await colCmd.ExecuteScalarAsync())!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MatchmakingTicket NewTicket(Guid userId) => MatchmakingTicket.Enqueue(
        userId, "smoke-game", 1, "multiplayer", 2, "blitz-3-2", false, "eu-west",
        MatchmakingTicket.ProvisionalRating, MatchmakingTicket.ProvisionalRatingSource, Guid.NewGuid(), T0);

    private static GameCapabilityProfile NewProfile(string gameSlug, int capabilityVersion = 1) =>
        GameCapabilityProfile.Create(
            gameSlug, capabilityVersion, 2, 4,
            new[] { "multiplayer", "ranked", "ai" },
            new[] { "blitz-3-2", "rapid-10-0" },
            new[] { "none", "sudden-death" },
            new[] { "Anyone", "FriendsOnly", "Disabled" },
            ratedEligible: true, aiFillEligible: true, "test-1");
}
