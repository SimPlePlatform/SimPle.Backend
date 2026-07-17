using Microsoft.EntityFrameworkCore;
using SimPle.Application.Chat;
using SimPle.Domain.Chat;
using SimPle.Domain.Users;
using SimPle.Infrastructure.Persistence;
using SimPle.Shared.Common;

namespace SimPle.Infrastructure.Chat;

public sealed class ChatRepository : IChatRepository
{
    private readonly AppDbContext _db;

    public ChatRepository(AppDbContext db) => _db = db;

    public Task<ChatMessage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.ChatMessages.FirstOrDefaultAsync(m => m.Id == id, ct);

    public Task<ChatMessage?> FindByClientCommandIdAsync(
        Guid senderId, Guid clientCommandId, CancellationToken ct = default) =>
        _db.ChatMessages.FirstOrDefaultAsync(
            m => m.SenderId == senderId && m.ClientCommandId == clientCommandId, ct);

    public async Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default)
    {
        await _db.ChatMessages.AddAsync(message, ct);

        try
        {
            await _db.SaveChangesAsync(ct);
            return message;
        }
        catch (DbUpdateException ex) when (PostgresContention.IsContention(ex))
        {
            _db.Entry(message).State = EntityState.Detached;
            return await _db.ChatMessages.AsNoTracking().FirstAsync(
                m => m.SenderId == message.SenderId && m.ClientCommandId == message.ClientCommandId, ct);
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryPageAsync(
        ChatScope scope,
        Guid scopeId,
        ChatHistoryDirection direction,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        int limit,
        CancellationToken ct = default)
    {
        var query = _db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Scope == scope && m.ScopeId == scopeId);

        if (direction == ChatHistoryDirection.Before)
        {
            if (cursorCreatedAtUtc is DateTime beforeAt && cursorId is Guid beforeId)
            {
                query = query.Where(m =>
                    m.CreatedAt < beforeAt || (m.CreatedAt == beforeAt && m.Id.CompareTo(beforeId) < 0));
            }

            var descPage = await query
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(limit)
                .ToListAsync(ct);

            descPage.Reverse();
            return descPage;
        }

        if (cursorCreatedAtUtc is DateTime afterAt && cursorId is Guid afterId)
        {
            query = query.Where(m =>
                m.CreatedAt > afterAt || (m.CreatedAt == afterAt && m.Id.CompareTo(afterId) > 0));
        }

        return await query
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetSendersAsync(
        IReadOnlyList<Guid> senderIds, CancellationToken ct = default)
    {
        if (senderIds.Count == 0) return new Dictionary<Guid, User>();

        var ids = senderIds.Distinct().ToArray();
        return await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
    }

    public Task SaveAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    // ── Retention sweep + hold seam (docs/specs/module-07-realtime-presence-chat-spec.md, "Cleanup vs moderation
    // hold", Risk #5) ───────────────────────────────────────────────────────────

    public async Task<int> DeleteExpiredAsync(DateTime nowUtc, int batchSize, CancellationToken ct = default)
    {
        // InMemory (unit tests) has neither raw SQL nor row locks. The SKIP LOCKED race this method exists for is
        // asserted where it can actually be proven — against real PostgreSQL (ChatRetentionHoldRaceTests).
        if (!_db.Database.IsRelational())
        {
            var heldIds = await _db.ChatMessageHolds
                .AsNoTracking()
                .Where(h => h.ReleasedAtUtc == null)
                .Select(h => h.MessageId)
                .ToListAsync(ct);

            var expired = await _db.ChatMessages
                .Where(m => m.RetainUntilUtc <= nowUtc && !heldIds.Contains(m.Id))
                .OrderBy(m => m.RetainUntilUtc)
                .Take(batchSize)
                .ToListAsync(ct);

            // Every hold row still attached to a candidate id is, by construction, released (an active one would
            // have excluded the message above via heldIds) — the FK is RESTRICT, so these must go with the
            // message or the delete is orphaning evidence rows pointing at nothing. Real PostgreSQL enforces this
            // as a 23503 (see the relational branch below); InMemory does not, but the cleanup responsibility is
            // identical either way.
            var candidateIds = expired.Select(m => m.Id).ToList();
            var releasedHolds = await _db.ChatMessageHolds
                .Where(h => candidateIds.Contains(h.MessageId))
                .ToListAsync(ct);

            _db.ChatMessageHolds.RemoveRange(releasedHolds);
            _db.ChatMessages.RemoveRange(expired);
            await _db.SaveChangesAsync(ct);
            return expired.Count;
        }

        // One bounded statement per batch, not ExecuteDeleteAsync() (EF 8's bulk delete cannot express LIMIT +
        // FOR UPDATE SKIP LOCKED) and not a select-then-delete pair (the window between those two statements is
        // precisely the hold race Risk #5 exists to close). A single (multi-CTE) statement is still one atomic
        // statement: the row locks "candidates" takes are held and consumed within that same statement, so there
        // is no gap for PlaceHoldAsync to race into. SKIP LOCKED means a row PlaceHoldAsync is mid-transaction on
        // is stepped over this pass, not blocked on and not deleted — it is picked up next cycle once uncontended.
        //
        // "candidates" already excludes any message with an ACTIVE hold (ReleasedAtUtc IS NULL) via the NOT EXISTS
        // probe above — but the FK chat_message_holds.MessageId -> chat_messages.Id is ON DELETE RESTRICT
        // unconditionally, so a RELEASED hold row (which the probe correctly ignores, per spec: "An active M12
        // hold prevents cleanup... until the copy/hold is acknowledged" — released is not active) still blocks the
        // message delete with 23503 unless it is removed in the same statement. "released_holds" is a second,
        // data-modifying CTE that does exactly that: Postgres always executes every data-modifying CTE in a WITH
        // clause to completion regardless of whether the primary statement reads its output, so this stays one
        // atomic, race-free operation rather than reopening a second select-then-delete window.
        return await _db.Database.ExecuteSqlAsync(
            $"""
            WITH candidates AS (
                SELECT m."Id"
                FROM chat_messages m
                WHERE m."RetainUntilUtc" <= {nowUtc}
                  AND NOT EXISTS (
                      SELECT 1 FROM chat_message_holds h
                      WHERE h."MessageId" = m."Id" AND h."ReleasedAtUtc" IS NULL
                  )
                ORDER BY m."RetainUntilUtc"
                LIMIT {batchSize}
                FOR UPDATE OF m SKIP LOCKED
            ),
            released_holds AS (
                DELETE FROM chat_message_holds h
                USING candidates c
                WHERE h."MessageId" = c."Id"
                RETURNING h."Id"
            )
            DELETE FROM chat_messages m
            USING candidates c
            WHERE m."Id" = c."Id"
            """, ct);
    }

    public async Task<Result<ChatMessageHold>> PlaceHoldAsync(
        Guid messageId, string reasonCode, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational())
        {
            // No concurrent writers to race in a unit test against InMemory — the row lock this method takes on
            // real PostgreSQL is asserted by ChatRetentionHoldRaceTests instead.
            var exists = await _db.ChatMessages.AnyAsync(m => m.Id == messageId, ct);
            if (!exists)
                return Result<ChatMessageHold>.Fail(ChatErrors.MessageExpired, "The message has already aged out of retention.");

            var placeholderHold = ChatMessageHold.Place(messageId, reasonCode, nowUtc);
            await _db.ChatMessageHolds.AddAsync(placeholderHold, ct);
            await _db.SaveChangesAsync(ct);
            return Result<ChatMessageHold>.Ok(placeholderHold);
        }

        // Blocking FOR UPDATE — deliberately not SKIP LOCKED. A hold request must wait its turn on a row the
        // sweep is mid-transaction on rather than give up, so the two race honestly (Risk #5): whichever side
        // commits first wins, and the loser gets a truthful answer instead of a silently lost/duplicated hold.
        // An explicit transaction is required here (unlike DeleteExpiredAsync's single statement) because the
        // lock must be held across the existence check AND the subsequent insert.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var lockedIds = await _db.Database
            .SqlQuery<Guid>($"""
                SELECT "Id"
                FROM chat_messages
                WHERE "Id" = {messageId}
                FOR UPDATE
                """)
            .ToListAsync(ct);

        if (lockedIds.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return Result<ChatMessageHold>.Fail(ChatErrors.MessageExpired, "The message has already aged out of retention.");
        }

        var hold = ChatMessageHold.Place(messageId, reasonCode, nowUtc);
        await _db.ChatMessageHolds.AddAsync(hold, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result<ChatMessageHold>.Ok(hold);
    }
}
