using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Capabilities;
using SimPle.Infrastructure.Games;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// The capability seeder (D2), against real PostgreSQL — the advisory lock, the checksum, and the FK to
/// <c>games.slug</c> only exist in a real database.
///
/// The seeder's most important property is that it fails CLOSED. A capability profile that permits seat counts or
/// modes Module 4's catalog does not would let a lobby be created that Module 5's engine cannot host — a failure
/// that would otherwise surface as a confused player clicking Start. The seeder refuses to write it.
/// </summary>
public sealed class GameCapabilitySeederTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_m6_seed_{Guid.NewGuid():N}";
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
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run seeder tests.");

    private AppDbContext CreateTestDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private GameCapabilitySeeder CreateSeeder(AppDbContext db) =>
        new(db, new FakeTimeProvider(new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<GameCapabilitySeeder>.Instance);

    private async Task SeedCatalogAsync()
    {
        await using var db = CreateTestDb();
        var seeder = new GameCatalogSeeder(db, NullLogger<GameCatalogSeeder>.Instance);
        var result = await seeder.SeedAsync();
        Assert.True(result.Success, result.Message);
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Seeder_AppliesEveryProfileOverASeededCatalog()
    {
        SkipIfNoPg();

        await SeedCatalogAsync();

        await using var db = CreateTestDb();
        var result = await CreateSeeder(db).SeedAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(8, result.ProfilesCreated);
        Assert.Equal(0, result.ProfilesUpdated);

        await using var verify = CreateTestDb();
        Assert.Equal(8, await verify.GameCapabilityProfiles.CountAsync());
        Assert.Equal(8, await verify.GameCapabilityProfiles.CountAsync(p => p.IsActive));
    }

    [SkippableFact]
    public async Task EverySeededProfileIsASubsetOfItsCatalogGame()
    {
        SkipIfNoPg();

        // The invariant that keeps M6 and M4 honest with each other. Verified against what actually landed in the
        // database, not against the manifest text.
        await SeedCatalogAsync();

        await using var db = CreateTestDb();
        await CreateSeeder(db).SeedAsync();

        await using var verify = CreateTestDb();
        var profiles = await verify.GameCapabilityProfiles.AsNoTracking().ToListAsync();
        var games = await verify.Games.AsNoTracking().Include(g => g.Capabilities).ToListAsync();

        foreach (var profile in profiles)
        {
            var game = games.Single(g => g.Slug == profile.GameSlug);

            var drift = profile.ContradictsCatalog(
                game.MinPlayers, game.MaxPlayers, game.Capabilities.Select(c => c.Mode));

            Assert.True(drift.Allowed, $"{profile.GameSlug}: {drift.Reason}");
        }
    }

    [SkippableFact]
    public async Task Seeder_RecordsItsManifestVersionAndChecksum()
    {
        SkipIfNoPg();

        await SeedCatalogAsync();

        await using var db = CreateTestDb();
        await CreateSeeder(db).SeedAsync();

        await using var verify = CreateTestDb();
        var history = await verify.CapabilitySeedHistory.AsNoTracking().SingleAsync();

        Assert.Equal("2026.1", history.ManifestVersion);
        Assert.Matches("^[0-9a-f]{64}$", history.Checksum);
        Assert.Equal(new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc), history.AppliedAtUtc);
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RerunningTheSeeder_IsANoOp()
    {
        SkipIfNoPg();

        // The seeder runs on every deploy. A second run must not duplicate profiles or bump anything.
        await SeedCatalogAsync();

        await using (var first = CreateTestDb())
            Assert.True((await CreateSeeder(first).SeedAsync()).Success);

        await using var db = CreateTestDb();
        var second = await CreateSeeder(db).SeedAsync();

        Assert.True(second.Success);
        Assert.Contains("no-op", second.Message);
        Assert.Equal(0, second.ProfilesCreated);
        Assert.Equal(0, second.ProfilesUpdated);

        await using var verify = CreateTestDb();
        Assert.Equal(8, await verify.GameCapabilityProfiles.CountAsync());
        Assert.Equal(1, await verify.CapabilitySeedHistory.CountAsync());
    }

    // ── Fail-closed paths ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Seeder_FailsClosedWhenTheCatalogHasNotBeenSeeded()
    {
        SkipIfNoPg();

        // Every profile is FK'd to games.slug. Seeding capabilities into an empty catalog must produce a clear
        // message naming the missing game, not an opaque foreign-key violation.
        await using var db = CreateTestDb();
        var result = await CreateSeeder(db).SeedAsync();

        Assert.False(result.Success);
        Assert.Contains("not in the Module 4 catalog", result.Message);

        await using var verify = CreateTestDb();
        Assert.Equal(0, await verify.GameCapabilityProfiles.CountAsync());
        Assert.Equal(0, await verify.CapabilitySeedHistory.CountAsync());
    }

    [SkippableFact]
    public async Task Seeder_FailsClosedOnAChecksumMismatch()
    {
        SkipIfNoPg();

        // Same manifest version, different content = someone edited a shipped manifest in place. Refusing to
        // overwrite is what stops a silent capability change from reaching lobbies already pinned to that version.
        await SeedCatalogAsync();

        await using (var first = CreateTestDb())
            Assert.True((await CreateSeeder(first).SeedAsync()).Success);

        // Corrupt the recorded checksum to simulate the manifest having changed under the same version.
        await using (var tamper = CreateTestDb())
        {
            var history = await tamper.CapabilitySeedHistory.SingleAsync();
            await tamper.Database.ExecuteSqlRawAsync(
                "UPDATE capability_seed_history SET \"Checksum\" = 'deadbeef' WHERE \"Id\" = {0}", history.Id);
        }

        await using var db = CreateTestDb();
        var result = await CreateSeeder(db).SeedAsync();

        Assert.False(result.Success);
        Assert.Contains("Checksum mismatch", result.Message);
    }

    [SkippableFact]
    public async Task Seeder_FailsClosedWhenAProfileContradictsTheCatalog()
    {
        SkipIfNoPg();

        // Simulate catalog drift: narrow a game's catalog bounds so the shipped profile no longer fits inside it.
        // The seeder must refuse rather than write a profile that permits a lobby M5's engine cannot host.
        await SeedCatalogAsync();

        await using (var drift = CreateTestDb())
        {
            // snake-rush ships as catalog 1..8; the profile pins 2..8. Shrink the catalog to 1..2.
            await drift.Database.ExecuteSqlRawAsync(
                "UPDATE games SET \"MaxPlayers\" = 2 WHERE \"Slug\" = 'snake-rush'");
        }

        await using var db = CreateTestDb();
        var result = await CreateSeeder(db).SeedAsync();

        Assert.False(result.Success);
        Assert.Contains("snake-rush", result.Message);
        Assert.Contains("exceed the catalog", result.Message);

        await using var verify = CreateTestDb();
        Assert.Equal(0, await verify.GameCapabilityProfiles.CountAsync());
        Assert.Equal(0, await verify.CapabilitySeedHistory.CountAsync());
    }

    [SkippableFact]
    public async Task Seeder_UsesADistinctAdvisoryLockFromTheCatalogSeeder()
    {
        SkipIfNoPg();

        // 44004001 belongs to GameCatalogSeeder. Sharing it would serialize two unrelated seeders against each
        // other for no reason. This asserts the capability seeder's lock is genuinely free while the catalog
        // seeder's is held — i.e. that they are different keys.
        await SeedCatalogAsync();

        await using var holder = new NpgsqlConnection(_testConn);
        await holder.OpenAsync();
        await using var holdCmd = holder.CreateCommand();
        holdCmd.CommandText = "SELECT pg_advisory_lock(44004001);";   // hold the CATALOG seeder's lock
        await holdCmd.ExecuteNonQueryAsync();

        try
        {
            // The capability seeder must still complete: it takes 44006001, not 44004001.
            await using var db = CreateTestDb();
            var result = await CreateSeeder(db).SeedAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(8, result.ProfilesCreated);
        }
        finally
        {
            await using var releaseCmd = holder.CreateCommand();
            releaseCmd.CommandText = "SELECT pg_advisory_unlock(44004001);";
            await releaseCmd.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Seeder_IsSafeUnderConcurrentRuns()
    {
        SkipIfNoPg();

        // Two deploy pods starting at once. The advisory lock serializes them; exactly one creates the profiles and
        // the other converges to a no-op. Neither may fail, and there must be no duplicates.
        await SeedCatalogAsync();

        async Task<CapabilitySeedResult> RunAsync()
        {
            await using var db = CreateTestDb();
            return await CreateSeeder(db).SeedAsync();
        }

        var results = await Task.WhenAll(RunAsync(), RunAsync());

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(8, results.Sum(r => r.ProfilesCreated));

        await using var verify = CreateTestDb();
        Assert.Equal(8, await verify.GameCapabilityProfiles.CountAsync());
        Assert.Equal(1, await verify.CapabilitySeedHistory.CountAsync());
    }
}
