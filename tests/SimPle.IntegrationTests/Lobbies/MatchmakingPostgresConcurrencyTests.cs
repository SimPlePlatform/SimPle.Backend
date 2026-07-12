using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Expiry;
using SimPle.Application.Friends.Outbox;
using SimPle.Application.Lobbies.DTOs;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.DTOs;
using SimPle.Application.Matchmaking.Outbox;
using SimPle.Application.Matchmaking.Services;
using SimPle.Application.Outbox;
using SimPle.Application.Outbox.Handlers;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Friends;
using SimPle.Domain.GameHost;
using SimPle.Domain.Games;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Users;
using SimPle.Application.GameHost.Services;
using SimPle.Infrastructure.Lobbies;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using Xunit;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// Real-PostgreSQL tests for slice 6C: the two-worker claim race, the partial unique index that makes double
/// assignment impossible, the cross-table one-active-lobby-<strong>or</strong>-ticket invariant, outbox atomicity,
/// the expiry sweep, the outbox dispatcher's lease, and the D3 benchmark.
///
/// <para>
/// None of these can run on InMemory, and that is the entire point of the class. InMemory enforces no filtered
/// unique index, no CHECK constraint, no row version, and has no <c>FOR UPDATE SKIP LOCKED</c> — so a two-worker
/// race asserted against it would pass whatever the code did, including code that assigned one ticket to two
/// matches. Every assertion below is about behavior only a real database can produce.
/// </para>
///
/// Skipped unless <c>MIGRATION_TEST_CONNECTION_STRING</c> points at a running PostgreSQL instance.
/// </summary>
public sealed class MatchmakingPostgresConcurrencyTests : IAsyncLifetime
{
    private const string TestKey = "test-lobby-credential-key-32-chars-min";

    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_m6c_{Guid.NewGuid():N}";
    private string? _testConn;

    private static readonly DateTime T0 = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

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
        // `AND usename = current_user` matters: under a least-privilege test role, terminating another role's
        // backend raises 42501, and an autovacuum worker (running as the bootstrap superuser) can appear on this
        // database at any moment. The unfiltered form fails intermittently and strands the database.
        terminateCmd.CommandText = $@"
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{_dbName}'
              AND pid <> pg_backend_pid()
              AND usename = current_user;";
        await terminateCmd.ExecuteNonQueryAsync();

        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\"";
        await dropCmd.ExecuteNonQueryAsync();
    }

    // ── THE test: two competing workers, zero duplicate assignment ───────────

    /// <summary>
    /// The guarantee the whole slice is built around (brief Risk #1).
    ///
    /// <para>
    /// <c>FOR UPDATE SKIP LOCKED</c> is <strong>not</strong> exclusivity — it stops two workers <em>contending</em>
    /// on one row, but a requeued ticket or a serialization retry can still attempt a second assignment. The partial
    /// unique index <c>ux_matchmaking_assignments_one_active_per_ticket</c> is the correctness boundary, and this
    /// test asserts against exactly that: whatever the two workers do, no ticket ends up with two active
    /// assignments, and no ticket is matched into two different groups.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task TwoCompetingWorkers_ProduceZeroDuplicateAssignment()
    {
        SkipIfNoPg();
        await SeedCatalogAsync();

        // 20 identical, mutually-compatible tickets: every one of them can pair with any other, which is the
        // worst case for two workers racing over the same pool.
        var tickets = await SeedQueuedTicketsAsync(count: 20);

        await using var dbA = CreateDb();
        await using var dbB = CreateDb();

        var workerA = BuildCoordinator(dbA, matchRuntimeAvailable: true);
        var workerB = BuildCoordinator(dbB, matchRuntimeAvailable: true);

        // Genuinely concurrent: two coordinators, two contexts, two connections.
        var cycles = await Task.WhenAll(
            workerA.RunCycleAsync("worker-A"),
            workerB.RunCycleAsync("worker-B"));

        await using var verify = CreateDb();

        // 1. No ticket has more than one ACTIVE assignment. This is the index's promise.
        var activeByTicket = await verify.MatchmakingAssignments
            .AsNoTracking()
            .Where(a => a.State == MatchmakingAssignmentState.Active)
            .GroupBy(a => a.TicketId)
            .Select(g => new { TicketId = g.Key, Count = g.Count() })
            .ToListAsync();

        activeByTicket.Should().OnlyContain(x => x.Count == 1, "no ticket may hold two active assignments");

        // 2. Every assignment belongs to a group of exactly the right size, and no ticket spans two groups.
        var assignments = await verify.MatchmakingAssignments.AsNoTracking().ToListAsync();
        assignments.Select(a => a.TicketId).Should().OnlyHaveUniqueItems(
            "a ticket assigned twice — even across groups — is a double-booked player");

        foreach (var group in assignments.GroupBy(a => a.GroupId))
        {
            group.Should().HaveCount(2, "every proposal in this fixture is a 2-player group");
            group.Select(a => a.MatchRequestId).Distinct().Should().ContainSingle(
                "one group means one match request");
        }

        // 3. Exactly one MatchRequestedV1 per group — never one per ticket.
        var events = await verify.OutboxMessages
            .AsNoTracking()
            .Where(m => m.EventType == MatchmakingOutbox.MatchRequested)
            .ToListAsync();

        events.Should().HaveCount(assignments.Select(a => a.GroupId).Distinct().Count());

        // 4. Every Matched ticket has an assignment, and every assigned ticket is Matched. A ticket marked Matched
        //    with nothing to hand to M8 is a player staring at a match that does not exist.
        var matched = await verify.MatchmakingTickets
            .AsNoTracking()
            .Where(t => t.State == MatchmakingTicketState.Matched)
            .Select(t => t.Id)
            .ToListAsync();

        matched.Should().BeEquivalentTo(assignments.Select(a => a.TicketId));

        // 5. Nothing was lost: every ticket is either matched or still queued for the next cycle.
        var stillQueued = await verify.MatchmakingTickets
            .AsNoTracking()
            .CountAsync(t => t.State == MatchmakingTicketState.Queued);

        (matched.Count + stillQueued).Should().Be(tickets.Count);

        // Sanity: the workers actually did work, so the assertions above are not vacuously true.
        cycles.Sum(c => c.TicketsMatched).Should().BeGreaterThan(0);
        matched.Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task ARequeuedTicketCannotAcquireASecondActiveAssignment_TheIndexRejectsIt()
    {
        // Proves the index is load-bearing rather than incidental. SKIP LOCKED cannot help here: there is no second
        // worker and no contention — just the same ticket being assigned twice, which is exactly what a
        // serialization retry or a requeue could attempt.
        SkipIfNoPg();
        await SeedCatalogAsync();

        var tickets = await SeedQueuedTicketsAsync(count: 2);

        await using var db = CreateDb();
        var ticketId = tickets[0];

        db.MatchmakingAssignments.Add(
            MatchmakingAssignment.Create(ticketId, Guid.NewGuid(), Guid.NewGuid(), T0));
        await db.SaveChangesAsync();

        db.MatchmakingAssignments.Add(
            MatchmakingAssignment.Create(ticketId, Guid.NewGuid(), Guid.NewGuid(), T0));

        (await CaptureSqlStateAsync(db)).Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [SkippableFact]
    public async Task ASupersededAssignmentFreesTheTicketForAFreshOne()
    {
        // The other half of the index's contract: `Active` is the only state it counts, so standing an assignment
        // down must genuinely release the slot — otherwise a failed M8 handoff could never be retried.
        SkipIfNoPg();
        await SeedCatalogAsync();

        var tickets = await SeedQueuedTicketsAsync(count: 1);
        var ticketId = tickets[0];

        await using var db = CreateDb();

        var first = MatchmakingAssignment.Create(ticketId, Guid.NewGuid(), Guid.NewGuid(), T0);
        db.MatchmakingAssignments.Add(first);
        await db.SaveChangesAsync();

        first.Supersede(T0.AddSeconds(1));
        db.MatchmakingAssignments.Add(
            MatchmakingAssignment.Create(ticketId, Guid.NewGuid(), Guid.NewGuid(), T0.AddSeconds(1)));

        var act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync();

        (await db.MatchmakingAssignments.AsNoTracking()
            .CountAsync(a => a.TicketId == ticketId && a.State == MatchmakingAssignmentState.Active))
            .Should().Be(1);
    }

    // ── Cross-table one-active-lobby-OR-ticket ──────────────────────────────

    /// <summary>
    /// Brief Risk #2. The filtered unique index on <c>lobby_members</c> and the one on <c>matchmaking_tickets</c>
    /// live on different tables and <strong>cannot see each other</strong>: under READ COMMITTED, a concurrent join
    /// and enqueue would each read "nothing active", neither seeing the other's uncommitted row, and both would
    /// commit.
    ///
    /// What closes that window is the transaction-scoped <c>pg_advisory_xact_lock</c> keyed on the actor, taken by
    /// <see cref="LobbyCommandRunner"/> — and, critically, taken by the <em>enqueue</em> path too. 6C's enqueue runs
    /// through the same runner for exactly this reason; had it opened its own transaction, this test would fail.
    /// </summary>
    [SkippableFact]
    public async Task ConcurrentJoinAndEnqueue_ByTheSameUser_ProduceExactlyOneWinner()
    {
        SkipIfNoPg();

        var host = await SeedUserAsync();
        var racer = await SeedUserAsync();
        await SeedCatalogAsync();

        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);
        var code = await SeedCredentialAsync(lobbyId, "JOIN-VS-QUEUE");

        await using var dbJoin = CreateDb();
        await using var dbQueue = CreateDb();

        var join = BuildLobbiesService(dbJoin).JoinByCredentialAsync(racer.Id, new JoinLobbyRequestDto(code, null));
        var enqueue = BuildMatchmakingService(dbQueue).EnqueueAsync(racer.Id, TicketRequest());

        var joinResult = await join;
        var enqueueResult = await enqueue;

        await using var verify = CreateDb();

        var seated = await verify.LobbyMembers.AsNoTracking()
            .CountAsync(m => m.UserId == racer.Id && m.State == LobbyMemberState.Joined);
        var queued = await verify.MatchmakingTickets.AsNoTracking()
            .CountAsync(t => t.UserId == racer.Id
                             && (t.State == MatchmakingTicketState.Queued
                                 || t.State == MatchmakingTicketState.Claimed));

        // Exactly one of the two committed. Which one wins is a genuine race and is not asserted — that the user
        // ends up in *both* is the bug, and it is the one thing that must never happen.
        (seated + queued).Should().Be(1, "a user holds one active lobby OR one active ticket, never both");

        // And the loser was told the truth, with a typed conflict rather than a 500.
        if (seated == 1)
        {
            enqueueResult.IsSuccess.Should().BeFalse();
            enqueueResult.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
        }
        else
        {
            joinResult.IsSuccess.Should().BeFalse();
            joinResult.Error!.Code.Should().Be(LobbyErrors.AlreadyActive);
        }
    }

    [SkippableFact]
    public async Task TheOneNonterminalTicketPerUserIndexRejectsASecondTicket()
    {
        SkipIfNoPg();
        await SeedCatalogAsync();

        var user = await SeedUserAsync();

        await using var db = CreateDb();

        db.MatchmakingTickets.Add(NewTicket(user.Id, T0));
        await db.SaveChangesAsync();

        db.MatchmakingTickets.Add(NewTicket(user.Id, T0));

        (await CaptureSqlStateAsync(db)).Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// Saves and returns the PostgreSQL SQLSTATE of the resulting violation. Asserting on the raw SQLSTATE rather
    /// than merely "it threw" is what makes these tests prove the <em>index</em> rejected the write, and not some
    /// unrelated failure that happens to also throw.
    /// </summary>
    private static async Task<string> CaptureSqlStateAsync(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg.SqlState;
        }

        throw new InvalidOperationException("Expected a unique-index violation, but the save succeeded.");
    }

    // ── Outbox atomicity ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AMatchedTicketAndItsMatchRequestedEventCommitTogether()
    {
        // Risk #6's storage half: an assignment with no event would leave M8 with a match nobody asked it to make;
        // an event with no assignment would hand M8 a request whose tickets are still queued.
        SkipIfNoPg();
        await SeedCatalogAsync();
        await SeedQueuedTicketsAsync(count: 2);

        await using var db = CreateDb();
        var result = await BuildCoordinator(db, matchRuntimeAvailable: true).RunCycleAsync("worker-1");

        result.TicketsMatched.Should().Be(2);

        await using var verify = CreateDb();

        var assignments = await verify.MatchmakingAssignments.AsNoTracking().ToListAsync();
        var events = await verify.OutboxMessages.AsNoTracking()
            .Where(m => m.EventType == MatchmakingOutbox.MatchRequested)
            .ToListAsync();

        assignments.Should().HaveCount(2);
        events.Should().ContainSingle();

        // The event names the group, and the group's assignments name the same match request.
        events[0].AggregateId.Should().Be(assignments[0].GroupId);
        assignments.Select(a => a.MatchRequestId).Distinct().Should().ContainSingle();

        // No credential, no rating, no region ever reaches an outbox payload.
        events[0].Payload.Should().NotContain("1200");
        events[0].Payload.Should().NotContain("eu-west");
    }

    [SkippableFact]
    public async Task WithNoMatchRuntime_TheWorkerCommitsNothingAtAll()
    {
        // The M8 gate, proven against the database rather than a substitute: not merely "no assignment returned",
        // but no row written anywhere.
        SkipIfNoPg();
        await SeedCatalogAsync();
        await SeedQueuedTicketsAsync(count: 4);

        await using var db = CreateDb();
        var result = await BuildCoordinator(db, matchRuntimeAvailable: false).RunCycleAsync("worker-1");

        result.Executed.Should().BeFalse();

        await using var verify = CreateDb();
        (await verify.MatchmakingAssignments.CountAsync()).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(m => m.EventType == MatchmakingOutbox.MatchRequested)).Should().Be(0);
        (await verify.MatchmakingTickets.CountAsync(t => t.State == MatchmakingTicketState.Queued)).Should().Be(4);
    }

    // ── Expiry sweep ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheExpirySweepTimesOutOverdueTicketsAndIsIdempotent()
    {
        SkipIfNoPg();
        await SeedCatalogAsync();
        await SeedQueuedTicketsAsync(count: 3, enqueuedAt: T0);

        await using var db = CreateDb();
        var clock = new FakeTimeProvider(T0.AddSeconds(63));
        var sweeper = BuildSweeper(db, clock);

        var first = await sweeper.SweepAsync();
        first.TicketsExpired.Should().Be(3);
        first.MaxTicketLag.Should().Be(TimeSpan.FromSeconds(3));

        // Re-running the sweep must not double-expire anything: TryTimeOut returns false rather than transitioning
        // a second time.
        var second = await sweeper.SweepAsync();
        second.TicketsExpired.Should().Be(0);

        await using var verify = CreateDb();
        (await verify.MatchmakingTickets.CountAsync(t => t.State == MatchmakingTicketState.TimedOut)).Should().Be(3);
    }

    [SkippableFact]
    public async Task AnExpiredTicketReleasesTheUsersSingleActiveSlot()
    {
        // The reason the sweep must actually write, rather than the read path merely treating the ticket as dead: a
        // ticket stuck Queued would keep its owner locked out of joining anything else, forever.
        SkipIfNoPg();
        await SeedCatalogAsync();

        var user = await SeedUserAsync();
        await using (var seed = CreateDb())
        {
            seed.MatchmakingTickets.Add(NewTicket(user.Id, T0));
            await seed.SaveChangesAsync();
        }

        await using var db = CreateDb();
        await BuildSweeper(db, new FakeTimeProvider(T0.AddSeconds(61))).SweepAsync();

        // The user can now queue again — the filtered unique index no longer sees a nonterminal ticket.
        await using var dbQueue = CreateDb();
        var result = await BuildMatchmakingService(dbQueue, new FakeTimeProvider(T0.AddSeconds(61)))
            .EnqueueAsync(user.Id, TicketRequest());

        result.IsSuccess.Should().BeTrue();
    }

    // ── Outbox dispatcher (D3) ──────────────────────────────────────────────

    [SkippableFact]
    public async Task TheDispatcherBackfillsDeliveryRows_ProcessesTheEvent_AndIsIdempotentOnReplay()
    {
        SkipIfNoPg();
        await SeedCatalogAsync();

        var host = await SeedUserAsync();
        var member = await SeedUserAsync();

        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);
        await SeedJoinedMemberAsync(lobbyId, member.Id);

        // A real M3 block event — emitted by the producer, with no delivery row, exactly as M3 has been writing them
        // since long before any consumer existed.
        await using (var seed = CreateDb())
        {
            seed.Blocks.Add(Block.Create(host.Id, member.Id));
            seed.OutboxMessages.Add(FriendOutbox.UserBlockedEvent(Block.Create(host.Id, member.Id)));
            await seed.SaveChangesAsync();
        }

        await using var db = CreateDb();
        var (processor, handler) = BuildDispatcher(db);

        var result = await processor.DispatchAsync(handler);

        result.Leased.Should().Be(1);
        result.Processed.Should().Be(1);
        result.Failed.Should().Be(0);

        await using var verify = CreateDb();

        // The host blocked the member, so the member is removed and the host keeps the lobby they own.
        var joined = await verify.LobbyMembers.AsNoTracking()
            .Where(m => m.LobbyId == lobbyId && m.State == LobbyMemberState.Joined)
            .Select(m => m.UserId)
            .ToListAsync();

        joined.Should().BeEquivalentTo(new[] { host.Id });

        var delivery = await verify.OutboxDeliveries.AsNoTracking().SingleAsync();
        delivery.Processed.Should().BeTrue();
        delivery.HandlerName.Should().Be("lobby-block");

        // A second pass leases nothing — the delivery row is the memory that keeps at-least-once from becoming
        // at-least-twice-visibly.
        await using var db2 = CreateDb();
        var (processor2, handler2) = BuildDispatcher(db2);
        var replay = await processor2.DispatchAsync(handler2);

        replay.Leased.Should().Be(0);
        replay.Processed.Should().Be(0);
    }

    [SkippableFact]
    public async Task TwoConcurrentDispatchers_NeverProcessTheSameEventTwice()
    {
        // The lease, proven under real contention. FOR UPDATE SKIP LOCKED is what makes the second dispatcher step
        // over the rows the first is holding rather than block behind them — and the unique (EventId, HandlerName)
        // index is what stops them both creating the delivery row in the first place.
        SkipIfNoPg();
        await SeedCatalogAsync();

        var host = await SeedUserAsync();
        var member = await SeedUserAsync();
        var lobbyId = await SeedLobbyAsync(host.Id, maxPlayers: 4);
        await SeedJoinedMemberAsync(lobbyId, member.Id);

        await using (var seed = CreateDb())
        {
            seed.Blocks.Add(Block.Create(host.Id, member.Id));
            seed.OutboxMessages.Add(FriendOutbox.UserBlockedEvent(Block.Create(host.Id, member.Id)));
            await seed.SaveChangesAsync();
        }

        await using var dbA = CreateDb();
        await using var dbB = CreateDb();
        var (processorA, handlerA) = BuildDispatcher(dbA);
        var (processorB, handlerB) = BuildDispatcher(dbB);

        // One of these may fail outright on a unique-violation while backfilling the same delivery row; that is
        // contention, not a bug, and the surviving pass still delivers the event. What must never happen is both
        // succeeding in *processing* it.
        var results = await Task.WhenAll(
            SafeDispatchAsync(processorA, handlerA),
            SafeDispatchAsync(processorB, handlerB));

        results.Sum(r => r?.Processed ?? 0).Should().BeLessThanOrEqualTo(1);

        await using var verify = CreateDb();
        (await verify.OutboxDeliveries.AsNoTracking().CountAsync()).Should().Be(1, "one event, one handler, one row");
    }

    // ── D3 benchmark ────────────────────────────────────────────────────────

    /// <summary>
    /// The brief's local benchmark profile: 50 concurrent ticket creators, two competing workers, zero duplicate
    /// assignment, matchmaking cycle p95 under one second, and expiry lag under five seconds.
    ///
    /// <para>
    /// <strong>This is local evidence, not an internet-scale SLO.</strong> It runs against a single PostgreSQL
    /// container on a developer machine with no network hop, no other tenants, and no cold caches. Its value is that
    /// it would catch an accidental O(n²) scan or a missing index at 50 tickets — not that it predicts production.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Benchmark_FiftyConcurrentCreators_TwoWorkers_NoDuplicateAssignment_P95UnderOneSecond()
    {
        SkipIfNoPg();
        await SeedCatalogAsync();

        // 50 users, each enqueueing concurrently through the real service — including the advisory lock and the
        // whole-command retry, so the measurement covers the real write path rather than a bare INSERT.
        var users = new List<User>();
        for (var i = 0; i < 50; i++) users.Add(await SeedUserAsync());

        var enqueueSw = Stopwatch.StartNew();

        var enqueues = users.Select(async user =>
        {
            await using var db = CreateDb();
            return await BuildMatchmakingService(db).EnqueueAsync(user.Id, TicketRequest());
        }).ToList();

        var enqueueResults = await Task.WhenAll(enqueues);
        enqueueSw.Stop();

        enqueueResults.Should().OnlyContain(r => r.IsSuccess, "every one of the 50 tickets must be accepted");

        // Two competing workers, run repeatedly until the queue drains, measuring each cycle.
        var cycleTimes = new List<double>();
        var totalMatched = 0;

        for (var round = 0; round < 30; round++)
        {
            await using var dbA = CreateDb();
            await using var dbB = CreateDb();
            var workerA = BuildCoordinator(dbA, matchRuntimeAvailable: true);
            var workerB = BuildCoordinator(dbB, matchRuntimeAvailable: true);

            var sw = Stopwatch.StartNew();
            var cycles = await Task.WhenAll(
                workerA.RunCycleAsync("bench-A"),
                workerB.RunCycleAsync("bench-B"));
            sw.Stop();

            cycleTimes.Add(sw.Elapsed.TotalMilliseconds);
            totalMatched += cycles.Sum(c => c.TicketsMatched);

            if (cycles.All(c => c.TicketsClaimed == 0)) break;
        }

        await using var verify = CreateDb();

        // ── Correctness first. A fast worker that double-books players is worthless. ──
        var assignments = await verify.MatchmakingAssignments.AsNoTracking().ToListAsync();
        assignments.Select(a => a.TicketId).Should().OnlyHaveUniqueItems("zero duplicate assignment");
        assignments.Should().HaveCount(50, "all 50 tickets pair up into 25 groups");

        var groups = assignments.GroupBy(a => a.GroupId).ToList();
        groups.Should().HaveCount(25);
        groups.Should().OnlyContain(g => g.Count() == 2);

        var events = await verify.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.EventType == MatchmakingOutbox.MatchRequested);
        events.Should().Be(25, "one MatchRequestedV1 per group, never one per ticket");

        totalMatched.Should().Be(50);

        // ── Then the budgets. ──
        var p95 = Percentile(cycleTimes, 0.95);
        p95.Should().BeLessThan(1000, "matchmaking cycle p95 must stay under 1s");

        // Expiry lag: how far past its deadline the sweep is when it reaches an overdue ticket.
        await SeedQueuedTicketsAsync(count: 5, enqueuedAt: T0, userPrefix: "lag");

        await using var dbSweep = CreateDb();
        var sweepSw = Stopwatch.StartNew();
        var sweep = await BuildSweeper(dbSweep, new FakeTimeProvider(T0.AddSeconds(60))).SweepAsync();
        sweepSw.Stop();

        sweep.TicketsExpired.Should().Be(5);
        sweepSw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "expiry lag budget");

        // Recorded honestly for the evidence report rather than only asserted.
        Console.WriteLine(
            $"[M6C BENCHMARK] 50 enqueues in {enqueueSw.Elapsed.TotalMilliseconds:F0}ms; " +
            $"cycles={cycleTimes.Count}; cycle p95={p95:F1}ms; max={cycleTimes.Max():F1}ms; " +
            $"matched={totalMatched}; groups={groups.Count}; sweep={sweepSw.Elapsed.TotalMilliseconds:F0}ms");
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static async Task<OutboxDispatchResult?> SafeDispatchAsync(IOutboxProcessor processor, IOutboxHandler handler)
    {
        try
        {
            return await processor.DispatchAsync(handler);
        }
        catch (DbUpdateException)
        {
            // Lost the backfill race on the unique (EventId, HandlerName) index. Expected under contention.
            return null;
        }
    }

    // ── Composition ─────────────────────────────────────────────────────────

    private void SkipIfNoPg() => Skip.If(_masterConn is null,
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run Module 6C race tests.");

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    /// <summary>
    /// The real coordinator over the real repository and the real worker transaction. Only the M8 probe is
    /// substituted — because M8 does not exist, and the gate it drives is the thing under test.
    /// </summary>
    private MatchmakingCoordinator BuildCoordinator(AppDbContext db, bool matchRuntimeAvailable)
    {
        var probe = Substitute.For<IMatchRuntimeProbe>();
        probe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(matchRuntimeAvailable);
        probe.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        return new MatchmakingCoordinator(
            new MatchmakingRepository(db),
            new WorkerTransaction(db, NullLogger<WorkerTransaction>.Instance),
            probe,
            Options.Create(new MatchmakingOptions()),
            TimeProvider.System,
            NullLogger<MatchmakingCoordinator>.Instance);
    }

    private ExpirySweeper BuildSweeper(AppDbContext db, TimeProvider clock) =>
        new(new MatchmakingRepository(db),
            new LobbyRepository(db),
            new WorkerTransaction(db, NullLogger<WorkerTransaction>.Instance),
            Options.Create(new ExpiryOptions()),
            clock,
            NullLogger<ExpirySweeper>.Instance);

    private (IOutboxProcessor Processor, IOutboxHandler Handler) BuildDispatcher(AppDbContext db)
    {
        var transaction = new WorkerTransaction(db, NullLogger<WorkerTransaction>.Instance);

        var processor = new OutboxProcessor(
            new OutboxRepository(db), transaction, Options.Create(new OutboxOptions()),
            TimeProvider.System, NullLogger<OutboxProcessor>.Instance);

        var handler = new LobbyBlockHandler(
            new LobbyRepository(db),
            new LobbyCommandRunner(db, NullLogger<LobbyCommandRunner>.Instance),
            TimeProvider.System,
            NullLogger<LobbyBlockHandler>.Instance);

        return (processor, handler);
    }

    private MatchmakingService BuildMatchmakingService(AppDbContext db, TimeProvider? clock = null)
    {
        var probe = Substitute.For<IMatchRuntimeProbe>();
        probe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        probe.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var chat = Substitute.For<IChatRuntimeProbe>();
        chat.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var ai = Substitute.For<IAiParticipantProbe>();
        ai.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        return new MatchmakingService(
            new MatchmakingRepository(db),
            new LobbyRepository(db),
            new LobbyCommandRunner(db, NullLogger<LobbyCommandRunner>.Instance),
            probe, chat, ai,
            new UserRepository(db),
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }),
            clock ?? TimeProvider.System,
            NullLogger<MatchmakingService>.Instance);
    }

    private LobbiesService BuildLobbiesService(AppDbContext db)
    {
        var hasher = new HmacLobbyCredentialHasher(
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }));

        var throttle = Substitute.For<ILobbyJoinThrottle>();
        throttle.GetRetryAfterUtcAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DateTime?)null);

        var probe = Substitute.For<IMatchRuntimeProbe>();
        probe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        probe.IsInActiveMatchAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var chat = Substitute.For<IChatRuntimeProbe>();
        chat.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var ai = Substitute.For<IAiParticipantProbe>();
        ai.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        var engines = Substitute.For<IGameRegistry>();
        engines.RegisteredDefinitions.Returns(Array.Empty<GameDefinitionMetadata>());

        return new LobbiesService(
            new LobbyRepository(db),
            new LobbyCommandRunner(db, NullLogger<LobbyCommandRunner>.Instance),
            hasher, throttle, probe, chat, ai, engines,
            new UserRepository(db),
            Substitute.For<IFileStorageService>(),
            Options.Create(new StorageOptions()),
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }),
            TimeProvider.System,
            NullLogger<LobbiesService>.Instance);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    private static CreateTicketRequestDto TicketRequest() => new(
        GameSlug: "chess-lite", CapabilityVersion: 1, Mode: "multiplayer", PlayerCount: 2,
        TimeControlId: "blitz-3-2", Rated: false, Region: "eu-west");

    private static MatchmakingTicket NewTicket(Guid userId, DateTime enqueuedAt) =>
        MatchmakingTicket.Enqueue(
            userId, "chess-lite", 1, "multiplayer", 2, "blitz-3-2", false, "eu-west",
            MatchmakingTicket.ProvisionalRating, MatchmakingTicket.ProvisionalRatingSource,
            Guid.NewGuid(), enqueuedAt);

    /// <summary>
    /// N identical, mutually-compatible queued tickets — the worst case for two workers racing over one pool.
    /// Written straight to the table rather than through the service so the fixture is not itself under test.
    /// </summary>
    private async Task<List<Guid>> SeedQueuedTicketsAsync(
        int count, DateTime? enqueuedAt = null, string userPrefix = "mm")
    {
        var ids = new List<Guid>(count);
        await using var db = CreateDb();

        for (var i = 0; i < count; i++)
        {
            var g = Guid.NewGuid();
            var user = User.Create($"{userPrefix}{g:N}"[..20], $"{userPrefix}{g:N}@test.io", "hash", "M6C User");
            db.Users.Add(user);

            var ticket = NewTicket(user.Id, enqueuedAt ?? DateTime.UtcNow.AddSeconds(-1));
            db.MatchmakingTickets.Add(ticket);
            ids.Add(ticket.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateDb();
        var g = Guid.NewGuid();
        var user = User.Create($"m6c{g:N}"[..20], $"m6c{g:N}@test.io", "hash", "M6C Race User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> SeedLobbyAsync(Guid hostId, int maxPlayers)
    {
        await using var db = CreateDb();

        var lobby = Lobby.Create(
            hostId,
            new LobbySettings("chess-lite", 1, LobbyPrivacy.Private, maxPlayers, "blitz-3-2", false,
                "eu-west", SpectatorPolicy.Anyone, "none", false),
            Guid.NewGuid(),
            DateTime.UtcNow);

        db.Lobbies.Add(lobby);
        await db.SaveChangesAsync();
        return lobby.Id;
    }

    private async Task SeedJoinedMemberAsync(Guid lobbyId, Guid userId)
    {
        await using var db = CreateDb();
        var lobby = await db.Lobbies.Include(l => l.Members).FirstAsync(l => l.Id == lobbyId);
        lobby.Join(userId, DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedCredentialAsync(Guid lobbyId, string plaintextCode)
    {
        await using var db = CreateDb();

        var hasher = new HmacLobbyCredentialHasher(
            Options.Create(new LobbyCredentialOptions { Key = TestKey, DefaultRegion = "eu-west" }));

        db.LobbyJoinCredentials.Add(LobbyJoinCredential.Issue(
            lobbyId, hasher.HashCode(plaintextCode), hasher.HashLinkToken(plaintextCode + "-link"), 1,
            DateTime.UtcNow));

        await db.SaveChangesAsync();
        return plaintextCode;
    }

    private async Task SeedCatalogAsync()
    {
        await using var db = CreateDb();

        if (await db.Games.AnyAsync(g => g.Slug == "chess-lite")) return;

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
}
