using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Realtime;
using SimPle.Application.Realtime.Authorization;
using SimPle.Application.Realtime.Contracts;
using SimPle.Domain.Chat;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Chat;

/// <summary>
/// Chat command/query implementation (docs/specs/module-07-realtime-presence-chat-spec.md). Reuses the same
/// <see cref="IRealtimeScopeAuthorizer"/> surface the hub uses for subscribe, so send/delete/history all
/// re-authorize against current owner data on every call — never a cached principal from connection time.
/// </summary>
public sealed class ChatService : IChatService
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 50;

    /// <summary>No precise retry-after instant is available from <see cref="IRealtimeRateLimiter.TryAcquireMessage"/>
    /// (bool-only contract) — this is a conservative synthesized value matching the documented 5-per-5s burst
    /// window, not a measured one.</summary>
    private static readonly TimeSpan RateLimitRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>Safe non-navigable placeholder (spec: "no username leak") for a sender absent from
    /// <see cref="IChatRepository.GetSendersAsync"/>'s result (deleted/unresolvable account) — never the real
    /// <see cref="PublicIdentityDto"/> fields.</summary>
    private static readonly PublicIdentityDto TombstoneSender = new(
        Guid.Empty, "deleted-user", "Chat participant", "??", "#6B7280", null, "Player");

    private readonly IChatRepository _chat;
    private readonly IReadOnlyDictionary<string, IRealtimeScopeAuthorizer> _authorizers;
    private readonly IRealtimeNotifier _notifier;
    private readonly IRealtimeRateLimiter _rateLimiter;
    private readonly IChatProfanityFilter _profanity;
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;
    private readonly ILobbyRepository _lobbies;
    private readonly IFriendRepository _friends;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatRepository chat,
        IEnumerable<IRealtimeScopeAuthorizer> authorizers,
        IRealtimeNotifier notifier,
        IRealtimeRateLimiter rateLimiter,
        IChatProfanityFilter profanity,
        IFileStorageService storage,
        IOptions<StorageOptions> storageOptions,
        ILobbyRepository lobbies,
        IFriendRepository friends,
        TimeProvider clock,
        ILogger<ChatService> logger)
    {
        _chat = chat;
        _authorizers = authorizers.ToDictionary(a => a.ScopeKind);
        _notifier = notifier;
        _rateLimiter = rateLimiter;
        _profanity = profanity;
        _storage = storage;
        _storageOptions = storageOptions.Value;
        _lobbies = lobbies;
        _friends = friends;
        _clock = clock;
        _logger = logger;
    }

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    public async Task<Result<ChatMessageDto>> SendAsync(
        Guid actorUserId, Guid lobbyId, string? body, Guid clientCommandId, CancellationToken ct = default)
    {
        if (!await IsAllowedAsync(actorUserId, lobbyId, RealtimeAction.Send, ct))
            return Result<ChatMessageDto>.Fail(ChatErrors.NotFound, "Lobby not found.");

        // Idempotency first: a client retry of the same command must never cost a second rate-limit slot or get
        // a different profanity/validation verdict the second time around.
        var existing = await _chat.FindByClientCommandIdAsync(actorUserId, clientCommandId, ct);
        if (existing is not null)
            return Result<ChatMessageDto>.Ok(await ToOwnMessageDtoAsync(existing, ct));

        if (!_rateLimiter.TryAcquireMessage(actorUserId))
        {
            return Result<ChatMessageDto>.Fail(new Error(
                ChatErrors.RateLimitExceeded, "You are sending messages too quickly.")
            {
                RetryAfterUtc = NowUtc + RateLimitRetryAfter,
            });
        }

        var normalized = ChatBodyNormalizer.Normalize(body);
        if (!normalized.IsSuccess)
            return Result<ChatMessageDto>.Fail(ChatErrors.InvalidBody, normalized.Error!.Message);

        if (_profanity.IsProfane(normalized.Value!))
            return Result<ChatMessageDto>.Fail(ChatErrors.ProfanityRejected, "That message isn't allowed.");

        var message = ChatMessage.Create(
            ChatScope.Lobby, lobbyId, actorUserId, normalized.Value!, clientCommandId, NowUtc);

        // AddAsync is itself idempotent against ux_chat_messages_sender_command: a concurrent duplicate send that
        // raced past the FindByClientCommandIdAsync check above hits the unique index instead of throwing, and
        // returns whichever row actually won.
        var persisted = await _chat.AddAsync(message, ct);

        var dto = await ToOwnMessageDtoAsync(persisted, ct);
        var recipients = await GetDeliverableRecipientsAsync(lobbyId, actorUserId, ct);
        await _notifier.NotifyChatMessageCreatedAsync(lobbyId, recipients, dto, ct);
        return Result<ChatMessageDto>.Ok(dto);
    }

    public async Task<Result> DeleteAsync(Guid actorUserId, Guid messageId, CancellationToken ct = default)
    {
        var message = await _chat.GetByIdAsync(messageId, ct);
        if (message is null)
            return Result.Fail(ChatErrors.NotFound, "Message not found.");

        if (!await IsAllowedAsync(actorUserId, message.ScopeId, RealtimeAction.Delete, ct))
            return Result.Fail(ChatErrors.NotFound, "Message not found.");

        if (message.SenderId != actorUserId)
            return Result.Fail(ChatErrors.Forbidden, "Only the author can delete this message.");

        message.Delete(actorUserId, NowUtc);
        await _chat.SaveAsync(ct);

        // Uses the message's own DeletedAtUtc (set on first delete, unchanged on a retried one) rather than
        // "now" directly, so a retried delete re-broadcasts the original tombstone instant, not a new one.
        var recipients = await GetDeliverableRecipientsAsync(message.ScopeId, actorUserId, ct);
        await _notifier.NotifyChatMessageDeletedAsync(
            message.ScopeId, recipients, message.Id, message.DeletedAtUtc!.Value, ct);
        return Result.Ok();
    }

    public async Task<Result<CursorPage<ChatMessageDto>>> GetHistoryAsync(
        Guid actorUserId,
        Guid lobbyId,
        ChatHistoryDirection direction,
        string? cursor,
        int? limit,
        CancellationToken ct = default)
    {
        var pageSize = limit ?? DefaultLimit;
        if (pageSize < 1 || pageSize > MaxLimit)
            return Result<CursorPage<ChatMessageDto>>.Fail(
                ChatErrors.ValidationFailed, "Page size must be between 1 and 50.");

        if (!await IsAllowedAsync(actorUserId, lobbyId, RealtimeAction.Subscribe, ct))
            return Result<CursorPage<ChatMessageDto>>.Fail(ChatErrors.NotFound, "Lobby not found.");

        DateTime? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeTimeId(cursor, out var createdAt, out var id))
                return Result<CursorPage<ChatMessageDto>>.Fail(
                    ChatErrors.InvalidCursor, "The pagination cursor is invalid.");
            cursorCreatedAt = createdAt;
            cursorId = id;
        }

        var rows = await _chat.GetHistoryPageAsync(
            ChatScope.Lobby, lobbyId, direction, cursorCreatedAt, cursorId, pageSize, ct);

        // Block-aware history (M07-F1-security fix, mirrors GetDeliverableRecipientsAsync's realtime
        // fan-out filter): a blocked co-member's messages are excluded from every history page — including
        // reconnect resync and a fresh page load, not just live broadcast — so "blocked users do not receive
        // or infer presence/chat" holds for REST history the same way it already holds for realtime delivery.
        var visibleRows = await FilterBlockedSendersAsync(actorUserId, rows, ct);

        var senders = await _chat.GetSendersAsync(visibleRows.Select(m => m.SenderId).Distinct().ToList(), ct);

        var items = new List<ChatMessageDto>(visibleRows.Count);
        foreach (var row in visibleRows)
            items.Add(ToMessageDto(row, await ResolveSenderAsync(row.SenderId, senders, ct)));

        // The cursor always advances past the last row the *query* saw in its own scan direction — for `Before`
        // (queried DESC, then reversed to ascending output) that is the oldest row returned, i.e. items[0]; for
        // `After` (queried ASC) that is the newest row returned, i.e. items[^1].
        string? next = null;
        if (rows.Count == pageSize)
        {
            var cursorRow = direction == ChatHistoryDirection.Before ? rows[0] : rows[^1];
            next = Cursor.EncodeTimeId(cursorRow.CreatedAt, cursorRow.Id);
        }

        return Result<CursorPage<ChatMessageDto>>.Ok(new CursorPage<ChatMessageDto>(items, next));
    }

    /// <summary>Block-aware fan-out list (M07-001 fix): every currently-joined lobby member receives the
    /// broadcast except one blocked (either direction) with <paramref name="senderId"/> — a plain
    /// group broadcast would otherwise deliver a blocked co-member's chat activity to the blocker (and vice
    /// versa). <paramref name="senderId"/> is always included so the sender's own other connections stay in
    /// sync.</summary>
    private async Task<IReadOnlyCollection<Guid>> GetDeliverableRecipientsAsync(
        Guid lobbyId, Guid senderId, CancellationToken ct)
    {
        var lobby = await _lobbies.GetByIdAsync(lobbyId, ct);
        if (lobby is null)
            return Array.Empty<Guid>();

        var recipients = new List<Guid>();
        foreach (var member in lobby.JoinedMembers)
        {
            if (member.UserId == senderId)
            {
                recipients.Add(member.UserId);
                continue;
            }

            if (!await _friends.IsBlockedInEitherDirectionAsync(senderId, member.UserId, ct))
                recipients.Add(member.UserId);
        }

        return recipients;
    }

    /// <summary>Excludes any row whose sender is blocked (either direction) with <paramref name="actorUserId"/>
    /// from a history page. The actor's own messages are always visible. Mirrors
    /// <see cref="GetDeliverableRecipientsAsync"/>'s per-member check; lobby history pages are bounded (max 50
    /// rows, capped distinct senders), so a per-sender pairwise check is cheap.</summary>
    private async Task<List<ChatMessage>> FilterBlockedSendersAsync(
        Guid actorUserId, IReadOnlyList<ChatMessage> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return new List<ChatMessage>();

        var blockedSenderIds = new HashSet<Guid>();
        foreach (var senderId in rows.Select(r => r.SenderId).Distinct())
        {
            if (senderId != actorUserId && await _friends.IsBlockedInEitherDirectionAsync(actorUserId, senderId, ct))
                blockedSenderIds.Add(senderId);
        }

        return blockedSenderIds.Count == 0
            ? rows.ToList()
            : rows.Where(r => !blockedSenderIds.Contains(r.SenderId)).ToList();
    }

    private async Task<bool> IsAllowedAsync(
        Guid actorUserId, Guid lobbyId, RealtimeAction action, CancellationToken ct)
    {
        if (!_authorizers.TryGetValue(RealtimeEnvelope.LobbyScope, out var authorizer))
            return false;

        var result = await authorizer.AuthorizeAsync(actorUserId, lobbyId, action, ct);
        return result.IsAllowed;
    }

    private async Task<ChatMessageDto> ToOwnMessageDtoAsync(ChatMessage message, CancellationToken ct)
    {
        var senders = await _chat.GetSendersAsync(new[] { message.SenderId }, ct);
        return ToMessageDto(message, await ResolveSenderAsync(message.SenderId, senders, ct));
    }

    /// <summary>A sender absent from the batch lookup (deleted/unresolvable account) renders the safe
    /// placeholder — <c>IChatRepository.GetSendersAsync</c>'s documented cue. Mirrors
    /// <c>LobbiesService.ToIdentityAsync</c> so a chat sender's avatar resolves through the same
    /// presigned-URL path as every other surface that lists a <see cref="PublicIdentityDto"/>.</summary>
    private async Task<PublicIdentityDto> ResolveSenderAsync(
        Guid senderId, IReadOnlyDictionary<Guid, User> senders, CancellationToken ct)
    {
        if (!senders.TryGetValue(senderId, out var user))
            return TombstoneSender;

        return new PublicIdentityDto(
            user.Id, user.Username, user.DisplayName, user.Initials, user.Color,
            await BuildAvatarUrlAsync(user.AvatarObjectKey, user.AvatarUrl, ct),
            user.ProfileType.ToString());
    }

    private async Task<string?> BuildAvatarUrlAsync(string? objectKey, string? fallbackUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            var expiry = TimeSpan.FromMinutes(_storageOptions.ReadUrlExpiryMinutes);
            return await _storage.CreatePresignedReadUrlAsync(objectKey, expiry, ct);
        }
        return fallbackUrl;
    }

    private static ChatMessageDto ToMessageDto(ChatMessage message, PublicIdentityDto sender) => new(
        message.Id,
        message.ScopeId,
        sender,
        message.IsDeleted ? null : message.Body,
        message.IsDeleted,
        message.CreatedAt,
        message.SchemaVersion);
}
