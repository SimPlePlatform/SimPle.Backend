using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using Xunit;

namespace SimPle.IntegrationTests.Friends;

/// <summary>
/// PostgreSQL-only smoke tests for the AddFriendsAndBlocks migration.
/// Skipped unless MIGRATION_TEST_CONNECTION_STRING is set to a running PostgreSQL instance.
/// Each test METHOD gets its own class instance (xUnit IAsyncLifetime pattern), so each test
/// has an isolated database that is created in InitializeAsync and dropped in DisposeAsync.
/// </summary>
public sealed class FriendsMigrationSmokeTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_smoke_{Guid.NewGuid():N}";
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

        await using var masterConn = new NpgsqlConnection(_masterConn);
        await masterConn.OpenAsync();

        await using var terminateCmd = masterConn.CreateCommand();
        terminateCmd.CommandText = $@"
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{_dbName}'
              AND pid <> pg_backend_pid();";
        await terminateCmd.ExecuteNonQueryAsync();

        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\"";
        await dropCmd.ExecuteNonQueryAsync();
    }

    private void SkipIfNoPg() => Skip.If(_masterConn is null,
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run smoke tests.");

    private AppDbContext CreateTestDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_testConn)
            .Options;
        return new AppDbContext(options);
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private async Task<User[]> SeedUsersAsync(int count = 1)
    {
        await using var db = CreateTestDb();
        var users = Enumerable.Range(0, count).Select(_ =>
        {
            var g = Guid.NewGuid();
            return User.Create($"smk{g:N}"[..24], $"smk{g:N}@test.io", "hash", "Smoke User");
        }).ToArray();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        return users;
    }

    // ── Migration health ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_AppliesCleanly()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var table in new[] { "friendships", "blocks", "user_friend_settings" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task Migration_AddsSendCapColumnsToFriendships()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var column in new[] { "LastSenderId", "SendCountInWindow", "SendWindowStartUtc" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_name = 'friendships' AND column_name = @c
                """;
            cmd.Parameters.AddWithValue("c", column);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task Migration_AddsPeopleSearchPrefixIndexes()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var index in new[] { "ix_users_normalizedusername_pattern", "ix_users_displayname_upper_pattern" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'users' AND indexname = @i";
            cmd.Parameters.AddWithValue("i", index);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    // ── Unordered-pair uniqueness ─────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_UpgradesLegacySocialSchema_WithoutDataLoss()
    {
        SkipIfNoPg();

        // Return to the pre-Module-3 schema, then reproduce the retired
        // 20260529154515_AddFriendsSocialGraph migration used by older dev DBs.
        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260528172556_FixProfileSocialIdentityAndUsernamePolicy");
        }

        var users = await SeedUsersAsync(4);
        var now = DateTime.UtcNow;
        var requestId = Guid.NewGuid();
        var friendshipId = Guid.NewGuid();
        var blockId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(_testConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE users
                    ADD COLUMN "FriendRequestPolicy" text NOT NULL DEFAULT 'Anyone';

                CREATE TABLE friend_requests (
                    "Id" uuid NOT NULL CONSTRAINT "PK_friend_requests" PRIMARY KEY,
                    "SenderUserId" uuid NOT NULL,
                    "ReceiverUserId" uuid NOT NULL,
                    "Status" character varying(20) NOT NULL,
                    "RespondedAtUtc" timestamp with time zone NULL,
                    "CancelledAtUtc" timestamp with time zone NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX "IX_friend_requests_ReceiverUserId_Status"
                    ON friend_requests ("ReceiverUserId", "Status");
                CREATE INDEX "IX_friend_requests_SenderUserId_ReceiverUserId_Status"
                    ON friend_requests ("SenderUserId", "ReceiverUserId", "Status");

                CREATE TABLE friendships (
                    "Id" uuid NOT NULL CONSTRAINT "PK_friendships" PRIMARY KEY,
                    "UserId" uuid NOT NULL,
                    "FriendUserId" uuid NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX "IX_friendships_FriendUserId"
                    ON friendships ("FriendUserId");
                CREATE UNIQUE INDEX "IX_friendships_UserId_FriendUserId"
                    ON friendships ("UserId", "FriendUserId");

                CREATE TABLE user_blocks (
                    "Id" uuid NOT NULL CONSTRAINT "PK_user_blocks" PRIMARY KEY,
                    "BlockerUserId" uuid NOT NULL,
                    "BlockedUserId" uuid NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE INDEX "IX_user_blocks_BlockedUserId"
                    ON user_blocks ("BlockedUserId");
                CREATE UNIQUE INDEX "IX_user_blocks_BlockerUserId_BlockedUserId"
                    ON user_blocks ("BlockerUserId", "BlockedUserId");

                INSERT INTO friend_requests
                    ("Id", "SenderUserId", "ReceiverUserId", "Status", "CreatedAt", "UpdatedAt")
                VALUES (@requestId, @requesterId, @addresseeId, 'Pending', @now, @now);

                INSERT INTO friendships
                    ("Id", "UserId", "FriendUserId", "CreatedAt", "UpdatedAt")
                VALUES (@friendshipId, @friendUserA, @friendUserB, @now, @now);

                INSERT INTO user_blocks
                    ("Id", "BlockerUserId", "BlockedUserId", "CreatedAt", "UpdatedAt")
                VALUES (@blockId, @blockerId, @blockedId, @now, @now);

                UPDATE users SET "FriendRequestPolicy" = 'Off' WHERE "Id" = @privacyUserId;

                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260529154515_AddFriendsSocialGraph', '8.0.11');
                """;
            cmd.Parameters.AddWithValue("requestId", requestId);
            cmd.Parameters.AddWithValue("requesterId", users[0].Id);
            cmd.Parameters.AddWithValue("addresseeId", users[1].Id);
            cmd.Parameters.AddWithValue("friendshipId", friendshipId);
            cmd.Parameters.AddWithValue("friendUserA", users[2].Id);
            cmd.Parameters.AddWithValue("friendUserB", users[3].Id);
            cmd.Parameters.AddWithValue("blockId", blockId);
            cmd.Parameters.AddWithValue("blockerId", users[0].Id);
            cmd.Parameters.AddWithValue("blockedId", users[2].Id);
            cmd.Parameters.AddWithValue("privacyUserId", users[1].Id);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
        }

        await using (var db = CreateTestDb())
        {
            var request = await db.Friendships.SingleAsync(f => f.Id == requestId);
            Assert.Equal(users[0].Id, request.RequesterId);
            Assert.Equal(users[1].Id, request.AddresseeId);
            Assert.Equal(FriendshipStatus.Pending, request.Status);

            var friendship = await db.Friendships.SingleAsync(f => f.Id == friendshipId);
            Assert.Equal(FriendshipStatus.Accepted, friendship.Status);

            var block = await db.Blocks.SingleAsync(b => b.Id == blockId);
            Assert.Equal(users[0].Id, block.BlockerId);
            Assert.Equal(users[2].Id, block.BlockedId);

            var settings = await db.UserFriendSettings.SingleAsync(s => s.UserId == users[1].Id);
            Assert.Equal(FriendRequestPrivacy.Off, settings.FriendRequestPrivacy);
        }

        await using (var conn = new NpgsqlConnection(_testConn))
        {
            await conn.OpenAsync();
            foreach (var table in new[]
                     {
                         "legacy_friendships_m03",
                         "legacy_friend_requests_m03",
                         "legacy_user_blocks_m03"
                     })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
                cmd.Parameters.AddWithValue("t", table);
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }
        }
    }

    [SkippableFact]
    public async Task Migration_UnorderedPairIndex_RejectsDuplicate()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2);
        await using var db = CreateTestDb();

        // A→B: should succeed
        db.Friendships.Add(Friendship.Request(users[0].Id, users[1].Id));
        await db.SaveChangesAsync();

        // B→A: violates LEAST/GREATEST expression index (same unordered pair)
        db.Friendships.Add(Friendship.Request(users[1].Id, users[0].Id));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23505",
            $"Expected unique_violation (23505), got: {(ex.InnerException as PostgresException)?.SqlState}");
    }

    // ── CHECK constraints ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_SelfFriendshipConstraint_Rejected()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(1);
        await using var db = CreateTestDb();

        db.Friendships.Add(Friendship.Request(users[0].Id, users[0].Id));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23514",
            $"Expected check_violation (23514), got: {(ex.InnerException as PostgresException)?.SqlState}");
    }

    [SkippableFact]
    public async Task Migration_SelfBlockConstraint_Rejected()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(1);
        await using var db = CreateTestDb();

        db.Blocks.Add(Block.Create(users[0].Id, users[0].Id));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23514",
            $"Expected check_violation (23514), got: {(ex.InnerException as PostgresException)?.SqlState}");
    }

    // ── Cascade delete (all 5 FK roles) ──────────────────────────────────────

    [SkippableFact]
    public async Task Migration_AccountDeletion_CascadesAllRoles()
    {
        SkipIfNoPg();

        // 5 users: A=to-delete, B, C, D, E
        var users = await SeedUsersAsync(5);
        var (A, B, C, D, E) = (users[0].Id, users[1].Id, users[2].Id, users[3].Id, users[4].Id);

        await using (var db = CreateTestDb())
        {
            // A as RequesterId: A→B
            db.Friendships.Add(Friendship.Request(A, B));
            // A as AddresseeId: C→A
            db.Friendships.Add(Friendship.Request(C, A));
            // A as BlockerId: A blocks D
            db.Blocks.Add(Block.Create(A, D));
            // A as BlockedId: E blocks A
            db.Blocks.Add(Block.Create(E, A));
            // A as UserId in settings
            db.UserFriendSettings.Add(UserFriendSettings.CreateDefault(A));
            await db.SaveChangesAsync();
        }

        // Delete A via EF — triggers ON DELETE CASCADE for all FK roles
        await using (var db = CreateTestDb())
        {
            var userA = await db.Users.FindAsync(A);
            db.Users.Remove(userA!);
            await db.SaveChangesAsync();
        }

        // Verify via fresh context: all of A's rows gone, B/C/D/E still exist
        await using var verifyDb = CreateTestDb();
        Assert.Equal(0, await verifyDb.Friendships.CountAsync(f => f.RequesterId == A || f.AddresseeId == A));
        Assert.Equal(0, await verifyDb.Blocks.CountAsync(b => b.BlockerId == A || b.BlockedId == A));
        Assert.Equal(0, await verifyDb.UserFriendSettings.CountAsync(s => s.UserId == A));
        Assert.Equal(4, await verifyDb.Users.CountAsync(u => new[] { B, C, D, E }.Contains(u.Id)));
    }

    // ── Migration rollback ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_RollbackCleanly()
    {
        SkipIfNoPg();

        // Roll back AddFriendsAndBlocks to the preceding migration
        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260528172556_FixProfileSocialIdentityAndUsernamePolicy");
        }

        // Verify Module 3 tables are gone
        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        foreach (var table in new[] { "friendships", "blocks", "user_friend_settings" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        // No re-apply: each test has its own isolated database, dropped by DisposeAsync.
    }

    // ── Provider-real query translation ──────────────────────────────────────

    [SkippableFact]
    public async Task Repository_GetFriendsPagedAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2);
        await using var db = CreateTestDb();

        var friendship = Friendship.Request(users[0].Id, users[1].Id);
        friendship.Accept(users[1].Id);
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync();

        var repo = new FriendRepository(db);
        var result = await repo.GetFriendsPageAsync(users[0].Id, null, 20, null, null);

        Assert.Single(result);
        Assert.Equal(users[1].Id, result[0].other.Id);
    }

    [SkippableFact]
    public async Task Repository_GetRequestsPagedAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2); // users[0]=requester, users[1]=addressee
        await using var db = CreateTestDb();

        db.Friendships.Add(Friendship.Request(users[0].Id, users[1].Id));
        await db.SaveChangesAsync();

        var repo = new FriendRepository(db);
        var result = await repo.GetRequestsPageAsync(users[1].Id, "incoming", 20, null, null);

        Assert.Single(result);
        Assert.Equal(users[0].Id, result[0].requester.Id);
        Assert.Equal(0, result[0].mutualCount); // no shared friends seeded
    }

    [SkippableFact]
    public async Task Repository_GetSuggestionsAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        // 6 users with distinct privacy/relationship scenarios
        var users = await SeedUsersAsync(6);
        var actor         = users[0]; // the actor requesting suggestions
        var anyone        = users[1]; // no settings row → defaults to Anyone → must be included
        var off           = users[2]; // Off privacy → must be excluded
        var fofNoMutual   = users[3]; // FriendsOfFriends, 0 mutuals with actor → must be excluded
        var fofWithMutual = users[4]; // FriendsOfFriends, 1 mutual → must be included, mutualCount=1
        var mutual        = users[5]; // actor's friend AND fofWithMutual's friend → excluded (already connected)

        await using var db = CreateTestDb();

        // actor ↔ mutual: accepted (mutual now actor's friend → excluded from suggestions)
        var f1 = Friendship.Request(actor.Id, mutual.Id);
        f1.Accept(mutual.Id);
        db.Friendships.Add(f1);

        // mutual ↔ fofWithMutual: accepted (gives fofWithMutual exactly 1 mutual with actor)
        var f2 = Friendship.Request(mutual.Id, fofWithMutual.Id);
        f2.Accept(fofWithMutual.Id);
        db.Friendships.Add(f2);

        var sOff = UserFriendSettings.CreateDefault(off.Id);
        sOff.UpdateSettings(FriendRequestPrivacy.Off, null, null);
        db.UserFriendSettings.Add(sOff);

        var sFofNo = UserFriendSettings.CreateDefault(fofNoMutual.Id);
        sFofNo.UpdateSettings(FriendRequestPrivacy.FriendsOfFriends, null, null);
        db.UserFriendSettings.Add(sFofNo);

        var sFofWith = UserFriendSettings.CreateDefault(fofWithMutual.Id);
        sFofWith.UpdateSettings(FriendRequestPrivacy.FriendsOfFriends, null, null);
        db.UserFriendSettings.Add(sFofWith);

        await db.SaveChangesAsync();

        var repo = new FriendRepository(db);
        var suggestions = await repo.GetSuggestionsAsync(actor.Id, 50);

        // no settings row → Anyone → included
        Assert.Contains(suggestions, s => s.user.Id == anyone.Id);

        // Off privacy → excluded
        Assert.DoesNotContain(suggestions, s => s.user.Id == off.Id);

        // FriendsOfFriends, zero mutuals with actor → excluded
        Assert.DoesNotContain(suggestions, s => s.user.Id == fofNoMutual.Id);

        // FriendsOfFriends, 1 mutual → included with correct mutual count
        var fofEntry = Assert.Single(suggestions.Where(s => s.user.Id == fofWithMutual.Id));
        Assert.Equal(1, fofEntry.mutualCount);

        // Already actor's friend → excluded from suggestions
        Assert.DoesNotContain(suggestions, s => s.user.Id == mutual.Id);
    }

    [SkippableFact]
    public async Task Repository_SearchPeopleAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2);
        await using var db = CreateTestDb();

        var repo = new FriendRepository(db);
        // The correlated MutualCount subquery (flagged as a translation risk in the reconciliation doc) must
        // fully server-translate against real Postgres; a client-eval fallback would throw here.
        var result = await repo.SearchPeopleAsync(users[0].Id, users[1].NormalizedUsername, 20, null, null, null);

        Assert.Single(result);
        Assert.Equal(users[1].Id, result[0].user.Id);
    }

    [SkippableFact]
    public async Task Repository_GetVisibleFriendsPageAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2);
        await using var db = CreateTestDb();

        var friendship = Friendship.Request(users[0].Id, users[1].Id);
        friendship.Accept(users[1].Id);
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync();

        var repo = new FriendRepository(db);
        var result = await repo.GetVisibleFriendsPageAsync(users[0].Id, users[1].Id, null, 20, null, null);

        Assert.Single(result);
        Assert.Equal(users[1].Id, result[0].Id);
    }

    [SkippableFact]
    public async Task Repository_GetVisibleMutualFriendsPageAsync_TranslatesSuccessfully()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(3); // [0]=viewer, [1]=target, [2]=shared friend
        await using var db = CreateTestDb();

        var f1 = Friendship.Request(users[0].Id, users[2].Id);
        f1.Accept(users[2].Id);
        db.Friendships.Add(f1);

        var f2 = Friendship.Request(users[1].Id, users[2].Id);
        f2.Accept(users[2].Id);
        db.Friendships.Add(f2);

        await db.SaveChangesAsync();

        var repo = new FriendRepository(db);
        var result = await repo.GetVisibleMutualFriendsPageAsync(users[0].Id, users[1].Id, 20, null, null);

        Assert.Single(result);
        Assert.Equal(users[2].Id, result[0].Id);
    }
}
