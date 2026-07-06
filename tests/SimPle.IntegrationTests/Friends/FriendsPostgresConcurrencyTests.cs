using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Application.Friends;
using SimPle.Application.Friends.Outbox;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using Xunit;

namespace SimPle.IntegrationTests.Friends;

/// <summary>
/// PostgreSQL-only verification of the Module 3 concurrency, atomicity, and query-plan invariants that EF
/// InMemory cannot prove (brief Risk #1): xmin optimistic concurrency, unordered-pair 23505 convergence,
/// accept/remove-vs-block races, transactional outbox staging + rollback, and keyset-index usage via EXPLAIN.
///
/// Skipped unless MIGRATION_TEST_CONNECTION_STRING is set to a running PostgreSQL instance. Each test METHOD
/// gets its own isolated database (xUnit IAsyncLifetime), created in InitializeAsync and dropped in DisposeAsync.
/// </summary>
public sealed class FriendsPostgresConcurrencyTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_pgc_{Guid.NewGuid():N}";
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
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run these tests.");

    private AppDbContext CreateTestDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private async Task<User[]> SeedUsersAsync(int count)
    {
        await using var db = CreateTestDb();
        var users = Enumerable.Range(0, count).Select(_ =>
        {
            var g = Guid.NewGuid();
            return User.Create($"pgc{g:N}"[..24], $"pgc{g:N}@test.io", "hash", "PG User");
        }).ToArray();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        return users;
    }

    // ── xmin optimistic concurrency (never 500) ───────────────────────────────

    [SkippableFact]
    public async Task Xmin_StaleUpdate_YieldsConcurrencyConflict_NeverThrows()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);

        // Persist a pending A→B request.
        await using (var seed = CreateTestDb())
        {
            seed.Friendships.Add(Friendship.Request(users[0].Id, users[1].Id));
            await seed.SaveChangesAsync();
        }

        // Two independent contexts load the SAME row (same xmin snapshot).
        await using var ctx1 = CreateTestDb();
        await using var ctx2 = CreateTestDb();
        var repo1 = new FriendRepository(ctx1);
        var repo2 = new FriendRepository(ctx2);

        var edge1 = await repo1.GetEdgeAsync(users[0].Id, users[1].Id);
        var edge2 = await repo2.GetEdgeAsync(users[0].Id, users[1].Id);

        // ctx1 accepts first → commits, bumping the row's xmin.
        edge1!.Accept(users[1].Id);
        var outcome1 = await repo1.TryUpdateFriendshipAsync(edge1, FriendOutbox.RequestAcceptedEvent(edge1));
        Assert.Equal(UpdateFriendshipOutcome.Updated, outcome1);

        // ctx2's write is now stale — must surface as a domain ConcurrencyConflict, not a raw exception/500.
        edge2!.Accept(users[1].Id);
        var outcome2 = await repo2.TryUpdateFriendshipAsync(edge2, FriendOutbox.RequestAcceptedEvent(edge2));
        Assert.Equal(UpdateFriendshipOutcome.ConcurrencyConflict, outcome2);
    }

    // ── Cross-send 23505 convergence (exactly one edge) ───────────────────────

    [SkippableFact]
    public async Task CrossSend_ConcurrentInsertSamePair_ExactlyOneWins_23505Converges()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);

        await using var ctx1 = CreateTestDb();
        await using var ctx2 = CreateTestDb();
        var repo1 = new FriendRepository(ctx1);
        var repo2 = new FriendRepository(ctx2);

        // Both directions of the SAME unordered pair race to insert.
        var aToB = Friendship.Request(users[0].Id, users[1].Id);
        var bToA = Friendship.Request(users[1].Id, users[0].Id);

        var r1 = await repo1.TryAddFriendshipAsync(aToB, FriendOutbox.RequestCreatedEvent(aToB));
        var r2 = await repo2.TryAddFriendshipAsync(bToA, FriendOutbox.RequestCreatedEvent(bToA));

        // Exactly one Added, one Conflict (23505 on ix_friendships_unordered_pair) — never an unhandled 500.
        var outcomes = new[] { r1, r2 };
        Assert.Single(outcomes, o => o == AddFriendshipOutcome.Added);
        Assert.Single(outcomes, o => o == AddFriendshipOutcome.Conflict);

        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Friendships.CountAsync(f =>
            (f.RequesterId == users[0].Id && f.AddresseeId == users[1].Id) ||
            (f.RequesterId == users[1].Id && f.AddresseeId == users[0].Id)));
    }

    // ── Transactional outbox: stage atomically on success ─────────────────────

    [SkippableFact]
    public async Task TryAddFriendship_StagesOutboxAtomically_OnSuccess()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);

        await using var ctx = CreateTestDb();
        var repo = new FriendRepository(ctx);
        var edge = Friendship.Request(users[0].Id, users[1].Id);

        var outcome = await repo.TryAddFriendshipAsync(edge, FriendOutbox.RequestCreatedEvent(edge));
        Assert.Equal(AddFriendshipOutcome.Added, outcome);

        // Aggregate row and its staged event are both committed in the same unit of work.
        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Friendships.CountAsync(f => f.Id == edge.Id));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m =>
            m.AggregateId == edge.Id && m.EventType == FriendOutbox.RequestCreated));
    }

    // ── Transactional outbox: rollback rolls back the staged event too ────────

    [SkippableFact]
    public async Task TryAddFriendship_Conflict_RollsBackStagedOutboxWithAggregate()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);

        // An edge for the pair already exists (committed).
        await using (var seed = CreateTestDb())
        {
            seed.Friendships.Add(Friendship.Request(users[0].Id, users[1].Id));
            await seed.SaveChangesAsync();
        }

        await using var ctx = CreateTestDb();
        var repo = new FriendRepository(ctx);

        // A conflicting insert for the same unordered pair, staged together with its outbox event.
        var conflicting = Friendship.Request(users[1].Id, users[0].Id);
        var conflictingEvent = FriendOutbox.RequestCreatedEvent(conflicting);

        var outcome = await repo.TryAddFriendshipAsync(conflicting, conflictingEvent);
        Assert.Equal(AddFriendshipOutcome.Conflict, outcome);

        // The whole unit of work rolled back: no orphaned friendship AND no orphaned outbox row.
        await using var verify = CreateTestDb();
        Assert.Equal(0, await verify.Friendships.CountAsync(f => f.Id == conflicting.Id));
        Assert.Equal(0, await verify.OutboxMessages.CountAsync(m => m.AggregateId == conflicting.Id));
    }

    // ── Block atomicity: end edge + insert block + stage both events together ──

    [SkippableFact]
    public async Task BlockAndCancelFriendship_AtomicallyEndsEdgeAndStagesBothEvents()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);
        var (blocker, blocked) = (users[0].Id, users[1].Id);

        // Establish an accepted friendship first.
        await using (var seed = CreateTestDb())
        {
            var f = Friendship.Request(blocker, blocked);
            f.Accept(blocked);
            seed.Friendships.Add(f);
            await seed.SaveChangesAsync();
        }

        await using var ctx = CreateTestDb();
        var repo = new FriendRepository(ctx);

        var edge = await repo.GetEdgeAsync(blocker, blocked);
        edge!.EndByBlock(blocker);
        var block = Block.Create(blocker, blocked);

        var outcome = await repo.BlockAndCancelFriendshipAsync(
            block, edge, FriendOutbox.UserBlockedEvent(block), FriendOutbox.FriendshipRemovedEvent(edge));
        Assert.Equal(AddBlockOutcome.Added, outcome);

        // All four effects committed together: block row, ended edge, and BOTH outbox events.
        await using var verify = CreateTestDb();
        Assert.Equal(1, await verify.Blocks.CountAsync(b => b.Id == block.Id));
        var persistedEdge = await verify.Friendships.SingleAsync(f => f.Id == edge.Id);
        Assert.NotEqual(FriendshipStatus.Accepted, persistedEdge.Status);
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m =>
            m.AggregateId == block.Id && m.EventType == FriendOutbox.UserBlocked));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m =>
            m.AggregateId == edge.Id && m.EventType == FriendOutbox.FriendshipRemoved));
    }

    // ── accept-vs-block race resolves for the COMMITTED block (via xmin) ───────

    [SkippableFact]
    public async Task AcceptVsBlock_Race_CommittedBlockWins_ViaXmin()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);
        var (requester, addressee) = (users[0].Id, users[1].Id);

        await using (var seed = CreateTestDb())
        {
            seed.Friendships.Add(Friendship.Request(requester, addressee));
            await seed.SaveChangesAsync();
        }

        await using var acceptCtx = CreateTestDb();
        await using var blockCtx = CreateTestDb();
        var acceptRepo = new FriendRepository(acceptCtx);
        var blockRepo = new FriendRepository(blockCtx);

        // Addressee has the pending edge loaded, about to accept.
        var toAccept = await acceptRepo.GetByIdAsync(
            (await acceptRepo.GetEdgeAsync(requester, addressee))!.Id);

        // Meanwhile the addressee blocks the requester → ends the same edge (bumps xmin) and commits.
        var toEnd = await blockRepo.GetEdgeAsync(requester, addressee);
        toEnd!.EndByBlock(addressee);
        var block = Block.Create(addressee, requester);
        var blockOutcome = await blockRepo.BlockAndCancelFriendshipAsync(
            block, toEnd, FriendOutbox.UserBlockedEvent(block), FriendOutbox.FriendshipRemovedEvent(toEnd));
        Assert.Equal(AddBlockOutcome.Added, blockOutcome);

        // The now-stale accept must lose to the committed block (ConcurrencyConflict), not clobber it or 500.
        toAccept!.Accept(addressee);
        var acceptOutcome = await acceptRepo.TryUpdateFriendshipAsync(
            toAccept, FriendOutbox.RequestAcceptedEvent(toAccept));
        Assert.Equal(UpdateFriendshipOutcome.ConcurrencyConflict, acceptOutcome);

        // Ground truth: the edge is ended (not Accepted) and the block stands.
        await using var verify = CreateTestDb();
        Assert.NotEqual(FriendshipStatus.Accepted, (await verify.Friendships.SingleAsync(f => f.Id == toEnd.Id)).Status);
        Assert.Equal(1, await verify.Blocks.CountAsync(b => b.Id == block.Id));
    }

    // ── remove-vs-block race resolves for the COMMITTED block (via xmin) ───────

    [SkippableFact]
    public async Task RemoveVsBlock_Race_CommittedBlockWins_ViaXmin()
    {
        SkipIfNoPg();
        var users = await SeedUsersAsync(2);
        var (a, b) = (users[0].Id, users[1].Id);

        await using (var seed = CreateTestDb())
        {
            var f = Friendship.Request(a, b);
            f.Accept(b);
            seed.Friendships.Add(f);
            await seed.SaveChangesAsync();
        }

        await using var removeCtx = CreateTestDb();
        await using var blockCtx = CreateTestDb();
        var removeRepo = new FriendRepository(removeCtx);
        var blockRepo = new FriendRepository(blockCtx);

        var toRemove = await removeCtx.Friendships.FirstAsync(f => f.RequesterId == a && f.AddresseeId == b);

        // b blocks a → ends the accepted edge (bumps xmin) and commits.
        var toEnd = await blockRepo.GetEdgeAsync(a, b);
        toEnd!.EndByBlock(b);
        var block = Block.Create(b, a);
        Assert.Equal(AddBlockOutcome.Added, await blockRepo.BlockAndCancelFriendshipAsync(
            block, toEnd, FriendOutbox.UserBlockedEvent(block), FriendOutbox.FriendshipRemovedEvent(toEnd)));

        // a's stale "remove friend" write loses to the committed block.
        toRemove.Remove(a);
        var removeOutcome = await removeRepo.TryUpdateFriendshipAsync(
            toRemove, FriendOutbox.FriendshipRemovedEvent(toRemove));
        Assert.Equal(UpdateFriendshipOutcome.ConcurrencyConflict, removeOutcome);
    }

    // ── EXPLAIN: keyset pagination uses the covering index (server-side, no offset) ──

    [SkippableFact]
    public async Task Explain_IncomingRequestsKeyset_UsesKeysetIndex()
    {
        SkipIfNoPg();
        // Seed one addressee with several pending incoming requests so a plan is meaningful.
        var users = await SeedUsersAsync(6);
        var addressee = users[0].Id;
        await using (var seed = CreateTestDb())
        {
            for (var i = 1; i < users.Length; i++)
                seed.Friendships.Add(Friendship.Request(users[i].Id, addressee));
            await seed.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(_testConn);
        await conn.OpenAsync();

        // Force the planner to reveal whether an index-based plan EXISTS for the keyset shape; if the covering
        // index (AddresseeId, Status, SentAt, Id) were missing, this would fall back to a Seq Scan even here.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SET enable_seqscan = off;";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var explain = conn.CreateCommand();
        explain.CommandText = """
            EXPLAIN (FORMAT TEXT)
            SELECT "Id", "RequesterId", "AddresseeId", "SentAt"
            FROM friendships
            WHERE "Status" = 'Pending' AND "AddresseeId" = @addressee
            ORDER BY "SentAt" DESC, "Id" DESC
            LIMIT 20;
            """;
        explain.Parameters.AddWithValue("addressee", addressee);

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.AppendLine(reader.GetString(0));

        var planText = plan.ToString();
        Assert.Contains("ix_friendships_addressee_status_sentat_id", planText);
        Assert.DoesNotContain("Seq Scan", planText);
    }
}
