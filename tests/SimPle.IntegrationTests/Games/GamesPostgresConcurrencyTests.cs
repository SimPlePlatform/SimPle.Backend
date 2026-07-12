using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Games;
using SimPle.Application.Games.Outbox;
using SimPle.Domain.Games;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using Xunit;

namespace SimPle.IntegrationTests.Games;

/// <summary>
/// PostgreSQL-only verification of the Module 4 concurrency and query-plan invariants that EF InMemory cannot
/// prove: favorite-row unique-pair convergence, outbox-driven refavorite/unfavorite race convergence,
/// keyset-index usage via EXPLAIN for the sorts whose ORDER BY matches a raw indexed column, and LIKE-wildcard
/// escaping for the free-text search filter (brief Risk #3).
///
/// Skipped unless MIGRATION_TEST_CONNECTION_STRING is set to a running PostgreSQL instance. Each test METHOD
/// gets its own isolated database (xUnit IAsyncLifetime), created in InitializeAsync and dropped in DisposeAsync.
/// </summary>
public sealed class GamesPostgresConcurrencyTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_games_pgc_{Guid.NewGuid():N}";
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

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateTestDb();
        var g = Guid.NewGuid();
        var user = User.Create($"gpgc{g:N}"[..24], $"gpgc{g:N}@test.io", "hash", "PG Games User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Game> SeedGameAsync(
        string slug, GameLifecycle lifecycle = GameLifecycle.Available, string name = "Test Game",
        GameDifficulty difficulty = GameDifficulty.Medium, int durationMin = 5, int sortOrder = 0,
        int? featuredRank = null, string category = "strategy", string[]? tags = null)
    {
        await using var db = CreateTestDb();
        var game = Game.Create(
            slug, name, "Summary.", "Rules.", difficulty, durationMin, durationMin + 20, 1, 2,
            lifecycle, featuredRank, sortOrder, "art-token", "#111111", "#222222",
            $"{name} abstract game artwork", "2026.1", category, tags ?? Array.Empty<string>(), Array.Empty<string>());
        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game;
    }

    // ── Favorite unique-pair convergence (23505) ────────────────────────────

    [SkippableFact]
    public async Task AddFavorite_ConcurrentInsertSamePair_ExactlyOneWins_23505Converges()
    {
        SkipIfNoPg();
        var user = await SeedUserAsync();
        var game = await SeedGameAsync("race-game");

        await using var ctx1 = CreateTestDb();
        await using var ctx2 = CreateTestDb();
        var repo1 = new GameRepository(ctx1);
        var repo2 = new GameRepository(ctx2);

        var fav1 = UserFavoriteGame.Favorite(user.Id, game.Id);
        var fav2 = UserFavoriteGame.Favorite(user.Id, game.Id);

        var r1 = await repo1.AddFavoriteAsync(fav1, GameOutbox.GameFavoritedEvent(fav1));
        var r2 = await repo2.AddFavoriteAsync(fav2, GameOutbox.GameFavoritedEvent(fav2));

        var outcomes = new[] { r1, r2 };
        Assert.Single(outcomes, o => o == AddFavoriteOutcome.Added);
        Assert.Single(outcomes, o => o == AddFavoriteOutcome.Conflict);

        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Set<UserFavoriteGame>().CountAsync(f => f.UserId == user.Id && f.GameId == game.Id));
    }

    [SkippableFact]
    public async Task AddFavorite_StagesOutboxAtomically_OnSuccess()
    {
        SkipIfNoPg();
        var user = await SeedUserAsync();
        var game = await SeedGameAsync("atomic-fav-game");

        await using var ctx = CreateTestDb();
        var repo = new GameRepository(ctx);
        var favorite = UserFavoriteGame.Favorite(user.Id, game.Id);

        var outcome = await repo.AddFavoriteAsync(favorite, GameOutbox.GameFavoritedEvent(favorite));
        Assert.Equal(AddFavoriteOutcome.Added, outcome);

        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Set<UserFavoriteGame>().CountAsync(f => f.Id == favorite.Id));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m =>
            m.AggregateId == favorite.Id && m.EventType == GameOutbox.GameFavorited));
    }

    // ── Refavorite race: outbox (AggregateId, EventType, AggregateDomainVersion) uniqueness is the only
    // conflict signal since UserFavoriteGame carries no xmin/row-version token of its own (documented gap) ──

    [SkippableFact]
    public async Task RefavoriteRace_TwoContextsReactivateSameInactiveRow_OneWinsOneConverges()
    {
        SkipIfNoPg();
        var user = await SeedUserAsync();
        var game = await SeedGameAsync("refav-race-game");

        await using (var seed = CreateTestDb())
        {
            var f = UserFavoriteGame.Favorite(user.Id, game.Id);
            f.Unfavorite();
            seed.Add(f);
            await seed.SaveChangesAsync();
        }

        await using var ctx1 = CreateTestDb();
        await using var ctx2 = CreateTestDb();
        var repo1 = new GameRepository(ctx1);
        var repo2 = new GameRepository(ctx2);

        var row1 = await repo1.GetFavoriteAsync(user.Id, game.Id);
        var row2 = await repo2.GetFavoriteAsync(user.Id, game.Id);
        row1!.Refavorite();
        row2!.Refavorite();

        var r1 = await repo1.UpdateFavoriteAsync(row1, GameOutbox.GameFavoritedEvent(row1));
        var r2 = await repo2.UpdateFavoriteAsync(row2, GameOutbox.GameFavoritedEvent(row2));

        var outcomes = new[] { r1, r2 };
        Assert.Single(outcomes, o => o == UpdateFavoriteOutcome.Updated);
        Assert.Single(outcomes, o => o == UpdateFavoriteOutcome.ConcurrencyConflict);

        await using var verify = CreateTestDb();
        var persisted = await verify.Set<UserFavoriteGame>().SingleAsync(f => f.UserId == user.Id && f.GameId == game.Id);
        Assert.True(persisted.IsActive);
    }

    // ── EXPLAIN: default-sort keyset query uses the raw covering index (no transform in the ORDER BY) ──

    [SkippableFact]
    public async Task Explain_DefaultSortKeyset_UsesDefaultOrderIndex()
    {
        SkipIfNoPg();
        for (var i = 0; i < 6; i++)
            await SeedGameAsync($"default-sort-{i}", sortOrder: i);

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SET enable_seqscan = off;";
            await setCmd.ExecuteNonQueryAsync();
        }

        // Mirrors GameRepository.GetCatalogPageAsync's default-sort ORDER BY (FeaturedRank, SortOrder, Slug) —
        // no case conversion or rank transform, so the raw composite index directly satisfies the sort.
        await using var explain = conn.CreateCommand();
        explain.CommandText = """
            EXPLAIN (FORMAT TEXT)
            SELECT "Id", "Slug", "FeaturedRank", "SortOrder"
            FROM games
            WHERE "Lifecycle" IN ('ComingSoon', 'Available', 'Maintenance')
            ORDER BY "FeaturedRank", "SortOrder", "Slug"
            LIMIT 24;
            """;

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(0));

        var planText = plan.ToString();
        Assert.Contains("ix_games_default_order", planText);
        Assert.DoesNotContain("Seq Scan", planText);
    }

    // ── EXPLAIN: duration-sort keyset query uses the raw covering index ──

    [SkippableFact]
    public async Task Explain_DurationSortKeyset_UsesDurationIndex()
    {
        SkipIfNoPg();
        for (var i = 0; i < 6; i++)
            await SeedGameAsync($"duration-sort-{i}", durationMin: i + 1);

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SET enable_seqscan = off;";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var explain = conn.CreateCommand();
        explain.CommandText = """
            EXPLAIN (FORMAT TEXT)
            SELECT "Id", "Slug", "EstimatedDurationMinMinutes"
            FROM games
            ORDER BY "EstimatedDurationMinMinutes", "Slug"
            LIMIT 24;
            """;

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(0));

        var planText = plan.ToString();
        Assert.Contains("ix_games_duration_slug", planText);
        Assert.DoesNotContain("Seq Scan", planText);
    }

    // ── EXPLAIN (diagnostic, not a pass/fail claim): Difficulty sort orders by a CASE-derived rank, which the
    // raw (Difficulty, Slug) index cannot satisfy directly. This documents the known gap rather than asserting
    // an index is used — a future perf pass should either add an expression index or drop this caveat.

    [SkippableFact]
    public async Task Explain_DifficultySortKeyset_CannotUseRawIndex_DocumentedGap()
    {
        SkipIfNoPg();
        for (var i = 0; i < 6; i++)
            await SeedGameAsync($"difficulty-sort-{i}", difficulty: (GameDifficulty)(i % 3));

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SET enable_seqscan = off;";
            await setCmd.ExecuteNonQueryAsync();
        }

        // Mirrors GameRepository's Difficulty-sort ORDER BY (CASE Easy=0/Hard=2/else=1), Slug.
        await using var explain = conn.CreateCommand();
        explain.CommandText = """
            EXPLAIN (FORMAT TEXT)
            SELECT "Id", "Slug", "Difficulty"
            FROM games
            ORDER BY (CASE WHEN "Difficulty" = 'Easy' THEN 0 WHEN "Difficulty" = 'Hard' THEN 2 ELSE 1 END), "Slug"
            LIMIT 24;
            """;

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(0));

        // No assertion on index usage here by design — ix_games_difficulty_slug is on the raw enum-as-string
        // column, not the CASE-derived rank, so it cannot satisfy this ORDER BY. Verified ground truth: even
        // with enable_seqscan=off (cost +1e10) Postgres still chooses Seq Scan, confirming no index can serve
        // this sort. This test exists to keep the gap visible (see the final evidence report's deviations).
        Assert.NotEmpty(plan.ToString());
    }

    // ── LIKE-wildcard escaping: %, _, \ in the search value must not be treated as pattern metacharacters ──

    [SkippableFact]
    public async Task Search_PercentWildcardInQuery_IsEscaped_MatchesOnlyLiteralSubstring()
    {
        SkipIfNoPg();
        // "100%" is a real name substring on one game; a naive unescaped LIKE '%100%%' would ALSO match
        // any name containing "100" followed by arbitrary characters, i.e. everything — proving escaping
        // requires a name that would spuriously match if % were treated as a wildcard instead of a literal.
        await SeedGameAsync("percent-literal", name: "Wins 100% Guaranteed");
        await SeedGameAsync("percent-decoy", name: "Wins 100X Guaranteed");

        await using var db = CreateTestDb();
        var repo = new GameRepository(db);
        var filter = new GameCatalogFilter(
            "100% GUARANTEED", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new[] { GameLifecycle.Available }, GameCatalogSortKey.Default, 10, null, null);

        var results = await repo.GetCatalogPageAsync(filter);

        Assert.Single(results, g => g.Slug == "percent-literal");
        Assert.DoesNotContain(results, g => g.Slug == "percent-decoy");
    }

    [SkippableFact]
    public async Task Search_UnderscoreWildcardInQuery_IsEscaped_MatchesOnlyLiteralSubstring()
    {
        SkipIfNoPg();
        // "_" would match any single character under a naive LIKE; "co_op" only matches the literal decoy
        // if the underscore were left unescaped ("coXop" would spuriously match too).
        await SeedGameAsync("underscore-literal", name: "Co_Op Puzzle");
        await SeedGameAsync("underscore-decoy", name: "CoXOp Puzzle");

        await using var db = CreateTestDb();
        var repo = new GameRepository(db);
        var filter = new GameCatalogFilter(
            "CO_OP PUZZLE", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new[] { GameLifecycle.Available }, GameCatalogSortKey.Default, 10, null, null);

        var results = await repo.GetCatalogPageAsync(filter);

        Assert.Single(results, g => g.Slug == "underscore-literal");
        Assert.DoesNotContain(results, g => g.Slug == "underscore-decoy");
    }
}
