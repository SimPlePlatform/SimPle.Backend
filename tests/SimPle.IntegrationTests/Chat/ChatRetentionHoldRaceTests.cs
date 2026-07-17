using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SimPle.Application.Chat;
using SimPle.Domain.Chat;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Chat;
using SimPle.Infrastructure.Persistence;
using Xunit;

namespace SimPle.IntegrationTests.Chat;

/// <summary>
/// Real-PostgreSQL tests for the moderation-hold-vs-retention race (docs/specs/module-07-realtime-presence-chat-
/// spec.md, Risk #5, "mandatory real-PostgreSQL test").
///
/// <para>
/// InMemory cannot prove any of this: it has no row locks and no <c>FOR UPDATE ... SKIP LOCKED</c>, so a race
/// asserted against it would pass regardless of whether <see cref="ChatRepository.DeleteExpiredAsync"/> and
/// <see cref="ChatRepository.PlaceHoldAsync"/> actually serialize on the message row.
/// </para>
///
/// Skipped unless <c>MIGRATION_TEST_CONNECTION_STRING</c> points at a running PostgreSQL instance.
/// </summary>
public sealed class ChatRetentionHoldRaceTests : IAsyncLifetime
{
    private readonly string? _masterConn = Environment.GetEnvironmentVariable("MIGRATION_TEST_CONNECTION_STRING");
    private readonly string _dbName = $"simple_m7b_{Guid.NewGuid():N}";
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
              -- Only this role's own backends; see LobbiesPostgresConcurrencyTests for why the unfiltered form
              -- fails intermittently under a least-privilege test role.
              AND usename = current_user;";
        await terminateCmd.ExecuteNonQueryAsync();

        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\"";
        await dropCmd.ExecuteNonQueryAsync();
    }

    // ── The Risk #5 races ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnExpiredMessageWithAnActiveHold_SurvivesTheSweep()
    {
        SkipIfNoPg();

        var sender = await SeedUserAsync();
        var messageId = await SeedExpiredMessageAsync(sender.Id);

        await using (var holdDb = CreateDb())
        {
            var holdResult = await new ChatRepository(holdDb)
                .PlaceHoldAsync(messageId, "m12-evidence", T0, CancellationToken.None);
            holdResult.IsSuccess.Should().BeTrue();
        }

        await using var sweepDb = CreateDb();
        var deleted = await new ChatRepository(sweepDb).DeleteExpiredAsync(T0, 100, CancellationToken.None);

        deleted.Should().Be(0, "an active hold must exclude the row from the sweep's candidate set entirely");

        await using var verify = CreateDb();
        (await verify.ChatMessages.AnyAsync(m => m.Id == messageId)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task AnExpiredMessageWithNoHold_IsDeletedBySweep()
    {
        SkipIfNoPg();

        var sender = await SeedUserAsync();
        var messageId = await SeedExpiredMessageAsync(sender.Id);

        await using var sweepDb = CreateDb();
        var deleted = await new ChatRepository(sweepDb).DeleteExpiredAsync(T0, 100, CancellationToken.None);

        deleted.Should().Be(1);

        await using var verify = CreateDb();
        (await verify.ChatMessages.AnyAsync(m => m.Id == messageId)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task AnExpiredMessageWhoseHoldWasReleased_IsDeletedBySweep()
    {
        SkipIfNoPg();

        var sender = await SeedUserAsync();
        var messageId = await SeedExpiredMessageAsync(sender.Id);

        await using (var holdDb = CreateDb())
        {
            var holdResult = await new ChatRepository(holdDb)
                .PlaceHoldAsync(messageId, "m12-evidence", T0, CancellationToken.None);
            holdResult.IsSuccess.Should().BeTrue();

            var hold = await holdDb.ChatMessageHolds.SingleAsync(h => h.MessageId == messageId);
            hold.Release(T0);
            await holdDb.SaveChangesAsync();
        }

        await using var sweepDb = CreateDb();
        var deleted = await new ChatRepository(sweepDb).DeleteExpiredAsync(T0, 100, CancellationToken.None);

        deleted.Should().Be(1, "a released hold no longer satisfies the partial index's ReleasedAtUtc IS NULL filter");
    }

    /// <summary>
    /// The mandatory race itself. The sweep takes its row lock and deletes the row within one uncommitted
    /// statement/transaction; a concurrent <see cref="ChatRepository.PlaceHoldAsync"/> on the same row must
    /// <strong>block</strong> rather than race past it — proving the two transactions serialize on the message
    /// row (spec: "Row-lock serialization"). Once the sweep commits, the blocked hold unblocks and receives a
    /// truthful <see cref="ChatErrors.MessageExpired"/>, never a silently "successful" hold on a row that no
    /// longer exists.
    /// </summary>
    [SkippableFact]
    public async Task SweepWinningTheRace_UnblocksAPendingHold_WithATypedMessageExpired_NeverSilentEvidenceLoss()
    {
        SkipIfNoPg();

        var sender = await SeedUserAsync();
        var messageId = await SeedExpiredMessageAsync(sender.Id);

        await using var sweepDb = CreateDb();
        await using var sweepTx = await sweepDb.Database.BeginTransactionAsync();

        // Runs the sweep's single CTE+DELETE statement inside the still-open sweepTx — the row is deleted but not
        // yet committed, so its lock is still held.
        var deleted = await new ChatRepository(sweepDb).DeleteExpiredAsync(T0, 100, CancellationToken.None);
        deleted.Should().Be(1);

        await using var holdDb = CreateDb();
        var holdTask = new ChatRepository(holdDb)
            .PlaceHoldAsync(messageId, "m12-evidence", T0, CancellationToken.None);

        // The hold must still be blocked a short while later — proving serialization, not a lost race.
        var finishedEarly = await Task.WhenAny(holdTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        finishedEarly.Should().NotBe(holdTask, "PlaceHoldAsync must block on the sweep's uncommitted row lock rather than race past it");

        await sweepTx.CommitAsync();

        var holdResult = await holdTask.WaitAsync(TimeSpan.FromSeconds(10));

        holdResult.IsSuccess.Should().BeFalse("the sweep already committed the delete by the time the hold's lock was granted");
        holdResult.Error!.Code.Should().Be(ChatErrors.MessageExpired);

        await using var verify = CreateDb();
        (await verify.ChatMessages.AnyAsync(m => m.Id == messageId)).Should().BeFalse();
        (await verify.ChatMessageHolds.AnyAsync(h => h.MessageId == messageId)).Should().BeFalse(
            "a hold must never be persisted for a message the sweep already deleted");
    }

    /// <summary>Reverse interleaving of the same race: the hold commits first, so the message survives and the
    /// sweep's own <c>NOT EXISTS</c> probe (not <c>SKIP LOCKED</c>) is what excludes it.</summary>
    [SkippableFact]
    public async Task HoldWinningTheRace_LeavesTheMessageSurviving_AndTheSweepReportsItSkipped()
    {
        SkipIfNoPg();

        var sender = await SeedUserAsync();
        var messageId = await SeedExpiredMessageAsync(sender.Id);

        await using (var holdDb = CreateDb())
        {
            var holdResult = await new ChatRepository(holdDb)
                .PlaceHoldAsync(messageId, "m12-evidence", T0, CancellationToken.None);
            holdResult.IsSuccess.Should().BeTrue();
        }

        await using var sweepDb = CreateDb();
        var deleted = await new ChatRepository(sweepDb).DeleteExpiredAsync(T0, 100, CancellationToken.None);

        deleted.Should().Be(0, "the held message must never even enter the sweep's candidate set");

        await using var verify = CreateDb();
        (await verify.ChatMessages.AnyAsync(m => m.Id == messageId)).Should().BeTrue();
        (await verify.ChatMessageHolds.AnyAsync(h => h.MessageId == messageId && h.ReleasedAtUtc == null)).Should().BeTrue();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private void SkipIfNoPg() => Skip.If(_masterConn is null,
        "Set MIGRATION_TEST_CONNECTION_STRING to a PostgreSQL connection string to run Module 7B retention race tests.");

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_testConn).Options);

    private async Task<User> SeedUserAsync()
    {
        await using var db = CreateDb();
        var g = Guid.NewGuid();
        var user = User.Create($"m7b{g:N}"[..20], $"m7b{g:N}@test.io", "hash", "M7B Race User");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A message whose RetainUntilUtc has already passed relative to <see cref="T0"/> — created 31 days
    /// before T0, so its 30-day RetainUntilUtc is one day in T0's past.</summary>
    private async Task<Guid> SeedExpiredMessageAsync(Guid senderId)
    {
        await using var db = CreateDb();
        var createdAtUtc = T0 - ChatMessage.RetentionPeriod - TimeSpan.FromDays(1);
        var message = ChatMessage.Create(
            ChatScope.Lobby, Guid.NewGuid(), senderId, "an expired chat message", Guid.NewGuid(), createdAtUtc);
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }
}
