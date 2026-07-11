using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SimPle.Domain.Games;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Games;

/// <summary>
/// PostgreSQL-only smoke tests for the AddGameCatalog migration. Skipped unless
/// MIGRATION_TEST_CONNECTION_STRING is set to a running PostgreSQL instance. Each test METHOD gets its own
/// isolated database (xUnit IAsyncLifetime), created in InitializeAsync and dropped in DisposeAsync.
/// </summary>
public sealed class GameCatalogMigrationTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_games_smoke_{Guid.NewGuid():N}";
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

    private AppDbContext CreateTestDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateTestDb();
        var g = Guid.NewGuid();
        var user = User.Create($"gsmk{g:N}"[..24], $"gsmk{g:N}@test.io", "hash", "Smoke User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Game ValidComingSoonGame(string slug, int? featuredRank = null) => Game.Create(
        slug, "Test Game", "Summary.", "Rules.", GameDifficulty.Easy,
        1, 5, 1, 2, GameLifecycle.ComingSoon, featuredRank, 0,
        "art-token", "#111111", "#222222", "Test Game abstract game artwork", "2026.1",
        "strategy", new[] { "puzzle" }, new[] { "solo" });

    // ── Migration health ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_AppliesCleanly()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        foreach (var table in new[]
                 { "games", "game_tags", "game_mode_capabilities", "user_favorite_games", "catalog_seed_history" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'games' AND indexname = @i";
        indexCmd.Parameters.AddWithValue("i", "ux_games_featured_rank_one");
        Assert.Equal(1L, (long)(await indexCmd.ExecuteScalarAsync())!);
    }

    // ── Partial unique index: at most one FeaturedRank = 1 ────────────────────

    [SkippableFact]
    public async Task PartialUniqueIndex_RejectsSecondFeaturedRankOne()
    {
        SkipIfNoPg();

        await using var db = CreateTestDb();
        db.Games.Add(ValidComingSoonGame("game-one", featuredRank: 1));
        await db.SaveChangesAsync();

        db.Games.Add(ValidComingSoonGame("game-two", featuredRank: 1));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23505",
            $"Expected unique_violation (23505), got: {(ex.InnerException as PostgresException)?.SqlState}");
    }

    // ── CHECK constraint: Draft/Retired cannot be featured ─────────────────────

    [SkippableFact]
    public async Task CheckConstraint_RejectsDraftGameWithFeaturedRank()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games
                ("Id", "Slug", "Name", "Summary", "RulesSummary", "Category", "Difficulty",
                 "EstimatedDurationMinMinutes", "EstimatedDurationMaxMinutes", "MinPlayers", "MaxPlayers",
                 "Lifecycle", "FeaturedRank", "SortOrder", "ArtToken", "ArtColorA", "ArtColorB", "ArtAltText",
                 "ManifestVersion", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, 'draft-featured', 'Draft Featured', 'Summary', 'Rules', 'strategy', 'Easy',
                 1, 5, 1, 2, 'Draft', 1, 0, 'art', '#111111', '#222222', 'alt',
                 '2026.1', now(), now());
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [SkippableFact]
    public async Task CheckConstraint_RejectsRetiredGameWithFeaturedRank()
    {
        SkipIfNoPg();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games
                ("Id", "Slug", "Name", "Summary", "RulesSummary", "Category", "Difficulty",
                 "EstimatedDurationMinMinutes", "EstimatedDurationMaxMinutes", "MinPlayers", "MaxPlayers",
                 "Lifecycle", "FeaturedRank", "SortOrder", "ArtToken", "ArtColorA", "ArtColorB", "ArtAltText",
                 "ManifestVersion", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, 'retired-featured', 'Retired Featured', 'Summary', 'Rules', 'strategy', 'Easy',
                 1, 5, 1, 2, 'Retired', 1, 0, 'art', '#111111', '#222222', 'alt',
                 '2026.1', now(), now());
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    // ── Child-row unique constraints ────────────────────────────────────────

    [SkippableFact]
    public async Task GameTags_UniqueConstraint_RejectsDuplicateValueOnSameGame()
    {
        SkipIfNoPg();

        await using var db = CreateTestDb();
        var game = ValidComingSoonGame("tag-dupe-game"); // seeded with tag "puzzle"
        db.Games.Add(game);
        await db.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_tags ("Id", "GameId", "Value", "CreatedAt", "UpdatedAt")
            VALUES (@id, @gameId, 'puzzle', now(), now());
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("gameId", game.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState);
    }

    [SkippableFact]
    public async Task GameModeCapabilities_UniqueConstraint_RejectsDuplicateModeOnSameGame()
    {
        SkipIfNoPg();

        await using var db = CreateTestDb();
        var game = ValidComingSoonGame("mode-dupe-game"); // seeded with mode "solo"
        db.Games.Add(game);
        await db.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_mode_capabilities ("Id", "GameId", "Mode", "CreatedAt", "UpdatedAt")
            VALUES (@id, @gameId, 'solo', now(), now());
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("gameId", game.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState);
    }

    [SkippableFact]
    public async Task UserFavoriteGames_UniqueConstraint_RejectsDuplicateUserGamePair()
    {
        SkipIfNoPg();

        var user = await SeedUserAsync();
        await using var db = CreateTestDb();
        var game = ValidComingSoonGame("favorite-dupe-game");
        db.Games.Add(game);
        await db.SaveChangesAsync();

        db.Set<UserFavoriteGame>().Add(UserFavoriteGame.Favorite(user.Id, game.Id));
        await db.SaveChangesAsync();

        db.Set<UserFavoriteGame>().Add(UserFavoriteGame.Favorite(user.Id, game.Id));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.InnerException is PostgresException pg && pg.SqlState == "23505");
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Migration_RollbackCleanly()
    {
        SkipIfNoPg();

        await using (var db = CreateTestDb())
        {
            var migrator = db.GetService<IMigrator>();
            // Roll back to the migration immediately preceding AddGameCatalog.
            await migrator.MigrateAsync("20260709094629_AddPeopleSearchAndSendCap");
        }

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        foreach (var table in new[]
                 { "games", "game_tags", "game_mode_capabilities", "user_favorite_games", "catalog_seed_history" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t";
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using var indexCmd = conn.CreateCommand();
        indexCmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @i";
        indexCmd.Parameters.AddWithValue("i", "ux_games_featured_rank_one");
        Assert.Equal(0L, (long)(await indexCmd.ExecuteScalarAsync())!);
    }

}
