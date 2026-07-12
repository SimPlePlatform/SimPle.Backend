using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Games;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Games;

/// <summary>
/// PostgreSQL-only verification of <see cref="GameCatalogSeeder"/>: clean apply, rerun idempotency (via
/// CatalogSeedHistory), fail-closed checksum mismatch, and advisory-lock convergence under concurrent runs.
/// Skipped unless MIGRATION_TEST_CONNECTION_STRING is set. Each test METHOD gets its own isolated database.
/// </summary>
public sealed class GameCatalogSeederTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_games_seed_{Guid.NewGuid():N}";
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
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run these tests.");

    private AppDbContext CreateTestDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    // ── Clean apply ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SeedAsync_CleanApply_Creates8Games()
    {
        SkipIfNoPg();

        await using var db = CreateTestDb();
        var seeder = new GameCatalogSeeder(db, NullLogger<GameCatalogSeeder>.Instance);

        var result = await seeder.SeedAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(8, result.GamesCreated);
        Assert.Equal(0, result.GamesUpdated);

        await using var verify = CreateTestDb();
        Assert.Equal(8, await verify.Games.CountAsync());
    }

    // ── Rerun idempotency ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SeedAsync_Rerun_IsNoOp_NoDuplicateHistoryRow()
    {
        SkipIfNoPg();

        await using (var db1 = CreateTestDb())
        {
            var seeder1 = new GameCatalogSeeder(db1, NullLogger<GameCatalogSeeder>.Instance);
            var first = await seeder1.SeedAsync();
            Assert.True(first.Success, first.Message);
        }

        await using (var db2 = CreateTestDb())
        {
            var seeder2 = new GameCatalogSeeder(db2, NullLogger<GameCatalogSeeder>.Instance);
            var second = await seeder2.SeedAsync();
            Assert.True(second.Success, second.Message);
            Assert.Equal(0, second.GamesCreated);
            Assert.Equal(0, second.GamesUpdated);
        }

        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Set<CatalogSeedHistory>().CountAsync());
        Assert.Equal(8, await verify.Games.CountAsync());
    }

    // ── Checksum mismatch fails closed ──────────────────────────────────────

    [SkippableFact]
    public async Task SeedAsync_ForgedHistoryWithWrongChecksum_FailsClosed_NoWrites()
    {
        SkipIfNoPg();

        await using (var db = CreateTestDb())
        {
            db.Set<CatalogSeedHistory>().Add(CatalogSeedHistory.Record("2026.1", new string('0', 64)));
            await db.SaveChangesAsync();
        }

        await using (var db = CreateTestDb())
        {
            var seeder = new GameCatalogSeeder(db, NullLogger<GameCatalogSeeder>.Instance);
            var result = await seeder.SeedAsync();

            Assert.False(result.Success);
            Assert.Equal(0, result.GamesCreated);
            Assert.Equal(0, result.GamesUpdated);
        }

        await using var verify = CreateTestDb();
        Assert.Equal(0, await verify.Games.CountAsync());
        Assert.Equal(1, await verify.Set<CatalogSeedHistory>().CountAsync());
    }

    // ── Advisory lock convergence under concurrency ─────────────────────────

    [SkippableFact]
    public async Task SeedAsync_TwoConcurrentSeeders_ConvergeToExactlyOneApply()
    {
        SkipIfNoPg();

        await using var db1 = CreateTestDb();
        await using var db2 = CreateTestDb();
        var seeder1 = new GameCatalogSeeder(db1, NullLogger<GameCatalogSeeder>.Instance);
        var seeder2 = new GameCatalogSeeder(db2, NullLogger<GameCatalogSeeder>.Instance);

        var results = await Task.WhenAll(seeder1.SeedAsync(), seeder2.SeedAsync());

        Assert.All(results, r => Assert.True(r.Success, r.Message));

        await using var verify = CreateTestDb();
        Assert.Equal(8, await verify.Games.CountAsync());
        Assert.Equal(1, await verify.Set<CatalogSeedHistory>().CountAsync());
    }

    // ── Featured rank ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SeedAsync_OnlyChessLite_HasFeaturedRankOne()
    {
        SkipIfNoPg();

        await using var db = CreateTestDb();
        var seeder = new GameCatalogSeeder(db, NullLogger<GameCatalogSeeder>.Instance);
        await seeder.SeedAsync();

        await using var verify = CreateTestDb();
        var featured = await verify.Games.Where(g => g.FeaturedRank == 1).ToListAsync();
        Assert.Single(featured);
        Assert.Equal("chess-lite", featured[0].Slug);

        var others = await verify.Games.Where(g => g.Slug != "chess-lite").ToListAsync();
        Assert.All(others, g => Assert.Null(g.FeaturedRank));
    }
}
