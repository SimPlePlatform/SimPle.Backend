using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SimPle.Domain.Friends;
using SimPle.Domain.Profiles;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Profiles;

/// <summary>
/// PostgreSQL-only smoke tests for the AddProfilePrivacyAndRetiredUsernames migration.
/// Skipped unless MIGRATION_TEST_CONNECTION_STRING is set to a running PostgreSQL instance.
/// Each test METHOD gets its own class instance (xUnit IAsyncLifetime pattern), so each test
/// has an isolated database that is created in InitializeAsync and dropped in DisposeAsync.
/// </summary>
public sealed class ProfilePrivacyMigrationSmokeTests : IAsyncLifetime
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
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run smoke tests.");

    private AppDbContext CreateTestDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_testConn)
            .Options;
        return new AppDbContext(options);
    }

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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'retired_usernames'";
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    // ── retired_usernames uniqueness ─────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_RetiredUsernameUniqueIndex_RejectsDuplicate()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(2);
        await using var db = CreateTestDb();

        db.RetiredUsernames.Add(RetiredUsername.Create("OLDHANDLE", users[0].Id));
        await db.SaveChangesAsync();

        // A second user retiring the same normalized handle must be rejected: retired handles are
        // permanently non-reassignable, so no two rows may share a NormalizedUsername.
        db.RetiredUsernames.Add(RetiredUsername.Create("OLDHANDLE", users[1].Id));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23505",
            $"Expected unique_violation (23505), got: {(ex.InnerException as PostgresException)?.SqlState}");
    }

    // ── No-FK survival of prior owner's account deletion ─────────────────────

    [SkippableFact]
    public async Task Migration_RetiredUsername_SurvivesPriorOwnerAccountDeletion()
    {
        SkipIfNoPg();

        var users = await SeedUsersAsync(1);
        var priorOwnerId = users[0].Id;

        await using (var db = CreateTestDb())
        {
            db.RetiredUsernames.Add(RetiredUsername.Create("RETIREDHANDLE", priorOwnerId));
            await db.SaveChangesAsync();
        }

        // Delete the prior owner's account via EF. RetiredUsername deliberately has no FK to User,
        // so this must succeed without cascading and the retired row must remain in place.
        await using (var db = CreateTestDb())
        {
            var user = await db.Users.FindAsync(priorOwnerId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        await using var verifyDb = CreateTestDb();
        var retired = await verifyDb.RetiredUsernames.SingleAsync(r => r.NormalizedUsername == "RETIREDHANDLE");
        Assert.Equal(priorOwnerId, retired.PriorOwnerUserId);
        Assert.Equal(0, await verifyDb.Users.CountAsync(u => u.Id == priorOwnerId));
    }

    // ── Backfill-default correctness ─────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_BackfillsExistingSettingsRows_WithValidEnumDefaults()
    {
        SkipIfNoPg();

        // Return to the pre-AddProfilePrivacyAndRetiredUsernames schema, where user_friend_settings
        // only has (Id, UserId, FriendRequestPrivacy, CreatedAt, UpdatedAt).
        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260705120243_HardenFriendsSocialGraph");
        }

        var users = await SeedUsersAsync(1);
        var settingsId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var conn = new NpgsqlConnection(_testConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_friend_settings
                    ("Id", "UserId", "FriendRequestPrivacy", "CreatedAt", "UpdatedAt")
                VALUES (@id, @userId, 'Anyone', @now, @now);
                """;
            cmd.Parameters.AddWithValue("id", settingsId);
            cmd.Parameters.AddWithValue("userId", users[0].Id);
            cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        // Apply the migration under test. Its backfill defaultValue for SearchVisibility and
        // FriendsListVisibility must be valid enum member names (not empty string), or this row's
        // HasConversion<string>() columns fail to deserialize below.
        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync();
        }

        await using var verifyDb = CreateTestDb();
        var settings = await verifyDb.UserFriendSettings.SingleAsync(s => s.Id == settingsId);
        Assert.Equal(SearchVisibility.Everyone, settings.SearchVisibility);
        Assert.Equal(FriendsListVisibility.Friends, settings.FriendsListVisibility);
        Assert.Equal(1, settings.PrivacyPolicyVersion);
        Assert.Equal(FriendRequestPrivacy.Anyone, settings.FriendRequestPrivacy);
    }

    // ── Migration rollback ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_RollbackCleanly()
    {
        SkipIfNoPg();

        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260705120243_HardenFriendsSocialGraph");
        }

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'retired_usernames'";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }
}
