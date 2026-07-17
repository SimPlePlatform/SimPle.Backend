using SimPle.Domain.Chat;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Chat;

/// <summary>Data access for chat persistence (docs/specs/module-07-realtime-presence-chat-spec.md, "Data Model").
/// </summary>
public interface IChatRepository
{
    Task<ChatMessage?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>The idempotency lookup: a duplicate send (client retry) with the same
    /// <c>(SenderId, ClientCommandId)</c> resolves here instead of ever reaching a unique-constraint race, and the
    /// caller returns the original message unchanged.</summary>
    Task<ChatMessage?> FindByClientCommandIdAsync(Guid senderId, Guid clientCommandId, CancellationToken ct = default);

    /// <summary>Idempotent insert against <c>ux_chat_messages_sender_command</c>: a concurrent duplicate send that
    /// races past <see cref="FindByClientCommandIdAsync"/>'s own check hits the unique index here instead of
    /// throwing — the row that actually won the race is returned either way, so the caller never needs to catch
    /// a persistence-specific exception.</summary>
    Task<ChatMessage> AddAsync(ChatMessage message, CancellationToken ct = default);

    /// <summary>Keyset (cursor) page always returned in <c>(CreatedAt, Id)</c> ascending order, matching
    /// <c>ix_chat_messages_scope_created_id</c>, regardless of <paramref name="direction"/>.
    /// <see cref="ChatHistoryDirection.Before"/> (scrollback): no cursor =&gt; the latest page; with a cursor
    /// =&gt; the page immediately older than it. <see cref="ChatHistoryDirection.After"/> (reconnect repair): no
    /// cursor =&gt; from the start of the scope's history; with a cursor =&gt; the page immediately newer than
    /// it.</summary>
    Task<IReadOnlyList<ChatMessage>> GetHistoryPageAsync(
        ChatScope scope,
        Guid scopeId,
        ChatHistoryDirection direction,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        int limit,
        CancellationToken ct = default);

    /// <summary>Batch sender identity resolution for DTO projection, keyed by user id. A sender absent from the
    /// result (deleted/unresolvable account) is the caller's cue to render a deleted-sender placeholder.</summary>
    Task<IReadOnlyDictionary<Guid, User>> GetSendersAsync(IReadOnlyList<Guid> senderIds, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>The retention sweep (docs/specs/module-07-realtime-presence-chat-spec.md, "Cleanup vs moderation
    /// hold"). Deletes up to <paramref name="batchSize"/> rows whose <c>RetainUntilUtc</c> has passed and which
    /// carry no active <see cref="ChatMessageHold"/>, in one bounded statement (CTE with <c>LIMIT</c> +
    /// <c>FOR UPDATE OF m SKIP LOCKED</c>, then <c>DELETE ... USING</c>) so a row mid-<see cref="PlaceHoldAsync"/>
    /// is skipped this pass rather than blocked on or deleted out from under it. Returns the number of rows
    /// deleted.</summary>
    Task<int> DeleteExpiredAsync(DateTime nowUtc, int batchSize, CancellationToken ct = default);

    /// <summary>The seam Module 7 builds but never calls (docs/specs/module-07-realtime-presence-chat-spec.md,
    /// "Data Model" and Risk #5) — Module 12 places a moderation hold through this before the retention sweep can
    /// delete the message. Takes the same row lock the sweep takes (blocking, not <c>SKIP LOCKED</c>, since a
    /// hold request must wait its turn rather than give up) so the two race honestly: if the sweep already
    /// committed the delete, this returns <see cref="ChatErrors.MessageExpired"/> instead of silently losing
    /// evidence.</summary>
    Task<Result<ChatMessageHold>> PlaceHoldAsync(
        Guid messageId, string reasonCode, DateTime nowUtc, CancellationToken ct = default);
}
