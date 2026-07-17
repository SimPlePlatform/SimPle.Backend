using SimPle.Application.Realtime.Contracts;
using SimPle.Shared.Common;

namespace SimPle.Application.Chat;

/// <summary>
/// The chat command/query surface (docs/specs/module-07-realtime-presence-chat-spec.md, "API Contract"). The sole
/// send/delete/history authorization path — both <c>RealtimeHub.SendLobbyMessage</c> and <c>ChatController</c>
/// call into this service rather than re-implementing scope authorization themselves.
/// </summary>
public interface IChatService
{
    /// <summary>Sends a lobby chat message. Idempotent on <paramref name="clientCommandId"/>: a retried send with
    /// the same <c>(actorUserId, clientCommandId)</c> pair returns the original message unchanged, before the
    /// rate limiter or profanity filter ever runs again.</summary>
    Task<Result<ChatMessageDto>> SendAsync(
        Guid actorUserId, Guid lobbyId, string? body, Guid clientCommandId, CancellationToken ct = default);

    /// <summary>Author-only tombstone delete. Idempotent — a retried delete on an already-deleted message is a
    /// no-op that still succeeds.</summary>
    Task<Result> DeleteAsync(Guid actorUserId, Guid messageId, CancellationToken ct = default);

    /// <summary>Keyset-paginated lobby chat history. <paramref name="limit"/> defaults to 30, capped at 50.</summary>
    Task<Result<CursorPage<ChatMessageDto>>> GetHistoryAsync(
        Guid actorUserId,
        Guid lobbyId,
        ChatHistoryDirection direction,
        string? cursor,
        int? limit,
        CancellationToken ct = default);
}
