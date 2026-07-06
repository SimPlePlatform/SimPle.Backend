using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.Friends.DTOs;
using SimPle.Application.Friends.Outbox;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Friends.Services;

public sealed class FriendsService : IFriendsService
{
    private readonly IFriendRepository _friends;
    private readonly IUserRepository _users;
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<FriendsService> _logger;

    // Requester re-request cooldowns set on the terminal transition and enforced on the send path.
    private static readonly TimeSpan DeclineCooldown = TimeSpan.FromDays(7);
    private static readonly TimeSpan CancelCooldown = TimeSpan.FromHours(24);
    private static readonly TimeSpan DismissalWindow = TimeSpan.FromDays(30);

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int MaxConvergeAttempts = 3;

    // Canonical error codes (reconciliation R12).
    private const string SelfRequest = "Friends.SelfRequest";
    private const string SelfBlock = "Friends.SelfBlock";
    private const string RequestsDisabled = "Friends.RequestsDisabled";
    private const string NotFriendOfFriend = "Friends.NotFriendOfFriend";
    private const string NotPending = "Friends.NotPending";
    private const string RequestCooldown = "Friends.RequestCooldown";
    private const string AlreadyFriends = "Friends.AlreadyFriends";
    private const string ConcurrencyConflict = "Friends.ConcurrencyConflict";
    private const string NotVisibleCode = "Profile.NotVisible";
    private const string InvalidCursor = "Pagination.InvalidCursor";
    private const string ValidationFailed = "Validation.Failed";

    public FriendsService(
        IFriendRepository friends,
        IUserRepository users,
        IFileStorageService storage,
        IOptions<StorageOptions> storageOptions,
        ILogger<FriendsService> logger)
    {
        _friends = friends;
        _users = users;
        _storage = storage;
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    // ── Summary ─────────────────────────────────────────────────────────────────

    public async Task<Result<FriendSummaryDto>> GetSummaryAsync(Guid actorId, CancellationToken ct = default)
    {
        var friendCount = await _friends.GetFriendCountAsync(actorId, ct);
        var incoming = await _friends.GetIncomingRequestCountAsync(actorId, ct);
        var outgoing = await _friends.GetOutgoingRequestCountAsync(actorId, ct);
        return Result<FriendSummaryDto>.Ok(new(friendCount, incoming, outgoing));
    }

    // ── Cursor lists ──────────────────────────────────────────────────────────────

    public async Task<Result<CursorPage<FriendDto>>> GetFriendsAsync(
        Guid actorId, string? query, int limit, string? cursor, CancellationToken ct = default)
    {
        limit = ClampLimit(limit);

        string? afterName = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeStringId(cursor, out var k, out var id))
                return Result<CursorPage<FriendDto>>.Fail(InvalidCursor, "The pagination cursor is invalid.");
            afterName = k;
            afterId = id;
        }

        var normalized = NormalizeFriendQuery(query, out var queryError);
        if (queryError is not null)
            return Result<CursorPage<FriendDto>>.Fail(ValidationFailed, queryError);

        var rows = await _friends.GetFriendsPageAsync(actorId, normalized, limit, afterName, afterId, ct);

        var items = new List<FriendDto>(rows.Count);
        foreach (var (f, other) in rows)
        {
            var avatar = await BuildAvatarUrlAsync(other.AvatarObjectKey, other.AvatarUrl, ct);
            items.Add(new FriendDto(
                other.Id, other.Username, other.DisplayName, other.Initials, other.Color, avatar,
                FriendsSince: f.AcceptedAt ?? f.UpdatedAt));
        }

        string? next = rows.Count == limit
            ? Cursor.EncodeStringId(rows[^1].other.DisplayName.ToUpperInvariant(), rows[^1].other.Id)
            : null;

        return Result<CursorPage<FriendDto>>.Ok(new CursorPage<FriendDto>(items, next));
    }

    public async Task<Result<CursorPage<FriendRequestDto>>> GetRequestsAsync(
        Guid actorId, string direction, int limit, string? cursor, CancellationToken ct = default)
    {
        limit = ClampLimit(limit);

        DateTime? afterSentAt = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeTimeId(cursor, out var t, out var id))
                return Result<CursorPage<FriendRequestDto>>.Fail(InvalidCursor, "The pagination cursor is invalid.");
            afterSentAt = t;
            afterId = id;
        }

        var rows = await _friends.GetRequestsPageAsync(actorId, direction, limit, afterSentAt, afterId, ct);

        var items = new List<FriendRequestDto>(rows.Count);
        foreach (var (f, requester, addressee, mutualCount) in rows)
            items.Add(await BuildRequestDtoAsync(f, requester, addressee, mutualCount, ct));

        string? next = rows.Count == limit
            ? Cursor.EncodeTimeId(rows[^1].f.SentAt, rows[^1].f.Id)
            : null;

        return Result<CursorPage<FriendRequestDto>>.Ok(new CursorPage<FriendRequestDto>(items, next));
    }

    public async Task<Result<CursorPage<BlockDto>>> GetBlocksAsync(
        Guid actorId, int limit, string? cursor, CancellationToken ct = default)
    {
        limit = ClampLimit(limit);

        DateTime? afterCreatedAt = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodeTimeId(cursor, out var t, out var id))
                return Result<CursorPage<BlockDto>>.Fail(InvalidCursor, "The pagination cursor is invalid.");
            afterCreatedAt = t;
            afterId = id;
        }

        var rows = await _friends.GetBlocksPageAsync(actorId, limit, afterCreatedAt, afterId, ct);

        var items = new List<BlockDto>(rows.Count);
        foreach (var (b, blocked) in rows)
        {
            var avatar = await BuildAvatarUrlAsync(blocked.AvatarObjectKey, blocked.AvatarUrl, ct);
            items.Add(new BlockDto(
                blocked.Id, blocked.Username, blocked.DisplayName, blocked.Initials, blocked.Color, avatar,
                b.CreatedAt));
        }

        string? next = rows.Count == limit
            ? Cursor.EncodeTimeId(rows[^1].b.CreatedAt, rows[^1].b.Id)
            : null;

        return Result<CursorPage<BlockDto>>.Ok(new CursorPage<BlockDto>(items, next));
    }

    // ── Send (outcomes + cross-accept + cooldown) ─────────────────────────────────

    public async Task<Result<SendFriendRequestResult>> SendFriendRequestAsync(
        Guid actorId, Guid targetUserId, CancellationToken ct = default)
    {
        if (actorId == targetUserId)
            return Result<SendFriendRequestResult>.Fail(SelfRequest, "You cannot send a friend request to yourself.");

        var target = await _users.GetByIdAsync(targetUserId, ct);
        if (target is null || target.IsAccountSuspended())
            return NotVisible<SendFriendRequestResult>();

        if (await _friends.IsBlockedInEitherDirectionAsync(actorId, targetUserId, ct))
            return NotVisible<SendFriendRequestResult>();

        // Private targets are invisible to non-friends (guessed id → indistinguishable 404).
        if (target.Visibility == ProfileVisibility.Private
            && !await _friends.AreFriendsAsync(actorId, targetUserId, ct))
            return NotVisible<SendFriendRequestResult>();

        var settings = await _friends.GetSettingsAsync(targetUserId, ct);
        var privacy = settings?.FriendRequestPrivacy ?? FriendRequestPrivacy.Anyone;
        if (privacy == FriendRequestPrivacy.Off)
            return Result<SendFriendRequestResult>.Fail(RequestsDisabled, "This user is not accepting friend requests.");
        if (privacy == FriendRequestPrivacy.FriendsOfFriends
            && await _friends.GetMutualFriendCountAsync(actorId, targetUserId, ct) == 0)
            return Result<SendFriendRequestResult>.Fail(NotFriendOfFriend, "This user only accepts requests from friends of friends.");

        var actor = await _users.GetByIdAsync(actorId, ct);
        if (actor is null) return NotVisible<SendFriendRequestResult>();

        for (var attempt = 0; attempt < MaxConvergeAttempts; attempt++)
        {
            var edge = await _friends.GetEdgeAsync(actorId, targetUserId, ct);

            if (edge is null)
            {
                var created = Friendship.Request(actorId, targetUserId);
                var add = await _friends.TryAddFriendshipAsync(created, FriendOutbox.RequestCreatedEvent(created), ct);
                if (add == AddFriendshipOutcome.Conflict) continue;   // racing insert won → re-read + converge
                _logger.LogInformation("Security: Friend request sent. ActorId={ActorId} TargetId={TargetId}", actorId, targetUserId);
                return await SendResultAsync(created, actor, target, SendFriendRequestResult.RequestCreated, ct);
            }

            switch (edge.Status)
            {
                case FriendshipStatus.Accepted:
                    return Result<SendFriendRequestResult>.Fail(AlreadyFriends, "You are already friends with this user.");

                case FriendshipStatus.Pending when edge.RequesterId == actorId:
                    return await SendResultAsync(edge, actor, target, SendFriendRequestResult.AlreadyPending, ct);

                case FriendshipStatus.Pending:
                    // Reverse pending → atomic accept (cross_request_accepted).
                    edge.Accept(actorId);
                    var accepted = await _friends.TryUpdateFriendshipAsync(edge, FriendOutbox.RequestAcceptedEvent(edge), ct);
                    if (accepted == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
                    _logger.LogInformation("Security: Friend request cross-accepted. ActorId={ActorId} TargetId={TargetId}", actorId, targetUserId);
                    return await SendResultAsync(edge, actor, target, SendFriendRequestResult.CrossRequestAccepted, ct);

                default: // Declined | Cancelled — reactivate a terminal row for a fresh cycle.
                    if (edge.NextRequestAllowedAt is DateTime until
                        && edge.RequesterId == actorId && DateTime.UtcNow < until)
                        return Result<SendFriendRequestResult>.Fail(
                            new Error(RequestCooldown, "You must wait before sending another request to this user.")
                            { RetryAfterUtc = until });

                    edge.Reactivate(actorId, targetUserId);
                    var reactivated = await _friends.TryUpdateFriendshipAsync(edge, FriendOutbox.RequestCreatedEvent(edge), ct);
                    if (reactivated == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
                    _logger.LogInformation("Security: Friend request sent. ActorId={ActorId} TargetId={TargetId}", actorId, targetUserId);
                    return await SendResultAsync(edge, actor, target, SendFriendRequestResult.RequestCreated, ct);
            }
        }

        return Result<SendFriendRequestResult>.Fail(ConcurrencyConflict, "A concurrent operation modified this request. Please retry.");
    }

    // ── Accept / Decline / Cancel / Remove (BOLA-safe) ────────────────────────────

    public async Task<Result<FriendRequestDto>> AcceptFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxConvergeAttempts; attempt++)
        {
            var f = await _friends.GetByIdAsync(requestId, ct);
            if (f is null || f.AddresseeId != actorId)   // only the addressee accepts; guessed id → 404
                return NotVisible<FriendRequestDto>();

            if (await _friends.IsBlockedInEitherDirectionAsync(f.RequesterId, f.AddresseeId, ct))
                return NotVisible<FriendRequestDto>();   // a committed block wins the accept-vs-block race

            if (f.Status == FriendshipStatus.Accepted)   // idempotent already_accepted
                return await RequestDtoAsync(f, ct);
            if (f.Status != FriendshipStatus.Pending)
                return Result<FriendRequestDto>.Fail(NotPending, "This request is not pending.");

            f.Accept(actorId);
            var outcome = await _friends.TryUpdateFriendshipAsync(f, FriendOutbox.RequestAcceptedEvent(f), ct);
            if (outcome == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
            _logger.LogInformation("Security: Friend request accepted. ActorId={ActorId} RequestId={RequestId}", actorId, requestId);
            return await RequestDtoAsync(f, ct);
        }

        return Result<FriendRequestDto>.Fail(ConcurrencyConflict, "A concurrent operation modified this request. Please retry.");
    }

    public async Task<Result<FriendRequestDto>> DeclineFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxConvergeAttempts; attempt++)
        {
            var f = await _friends.GetByIdAsync(requestId, ct);
            if (f is null || f.AddresseeId != actorId)   // only the addressee declines; guessed id → 404
                return NotVisible<FriendRequestDto>();

            if (f.Status == FriendshipStatus.Declined)   // idempotent already_declined
                return await RequestDtoAsync(f, ct);
            if (f.Status != FriendshipStatus.Pending)
                return Result<FriendRequestDto>.Fail(NotPending, "This request is not pending.");

            f.Decline(actorId, DateTime.UtcNow + DeclineCooldown);
            var outcome = await _friends.TryUpdateFriendshipAsync(f, FriendOutbox.RequestDeclinedEvent(f), ct);
            if (outcome == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
            _logger.LogInformation("Security: Friend request declined. ActorId={ActorId} RequestId={RequestId}", actorId, requestId);
            return await RequestDtoAsync(f, ct);
        }

        return Result<FriendRequestDto>.Fail(ConcurrencyConflict, "A concurrent operation modified this request. Please retry.");
    }

    public async Task<Result> CancelFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxConvergeAttempts; attempt++)
        {
            var f = await _friends.GetByIdAsync(requestId, ct);
            if (f is null || f.RequesterId != actorId)   // only the requester cancels; guessed id → 404
                return Result.Fail(NotVisibleCode, "The requested resource was not found.");

            // Idempotent repeat cancel: already Cancelled by an earlier cancel (not a remove/block).
            if (f.Status == FriendshipStatus.Cancelled && f.EndReason is null)
                return Result.Ok();
            if (f.Status != FriendshipStatus.Pending)
                return Result.Fail(NotVisibleCode, "The requested resource was not found.");

            f.Cancel(actorId, DateTime.UtcNow + CancelCooldown);
            var outcome = await _friends.TryUpdateFriendshipAsync(f, FriendOutbox.RequestCancelledEvent(f), ct);
            if (outcome == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
            _logger.LogInformation("Security: Friend request cancelled. ActorId={ActorId} RequestId={RequestId}", actorId, requestId);
            return Result.Ok();
        }

        return Result.Fail(ConcurrencyConflict, "A concurrent operation modified this request. Please retry.");
    }

    public async Task<Result> RemoveFriendAsync(
        Guid actorId, Guid friendUserId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxConvergeAttempts; attempt++)
        {
            var edge = await _friends.GetEdgeAsync(actorId, friendUserId, ct);
            if (edge is null)                                   // unrelated pair → privacy-safe 404
                return Result.Fail(NotVisibleCode, "The requested resource was not found.");

            if (edge.Status == FriendshipStatus.Accepted)
            {
                edge.Remove(actorId);
                var outcome = await _friends.TryUpdateFriendshipAsync(edge, FriendOutbox.FriendshipRemovedEvent(edge), ct);
                if (outcome == UpdateFriendshipOutcome.ConcurrencyConflict) continue;
                _logger.LogInformation("Security: Friend removed. ActorId={ActorId} FriendId={FriendId}", actorId, friendUserId);
                return Result.Ok();
            }

            if (edge.Status == FriendshipStatus.Pending)        // not friends yet
                return Result.Fail(NotVisibleCode, "The requested resource was not found.");

            return Result.Ok();                                 // terminal + caller was a participant → idempotent 204
        }

        return Result.Fail(ConcurrencyConflict, "A concurrent operation modified this friendship. Please retry.");
    }

    // ── Suggestions / Discovery / Dismissal ───────────────────────────────────────

    public async Task<Result<IReadOnlyList<FriendSuggestionDto>>> GetSuggestionsAsync(
        Guid actorId, int limit, CancellationToken ct = default)
    {
        var suggestions = await _friends.GetSuggestionsAsync(actorId, Math.Clamp(limit, 1, 20), ct);

        var dtos = new List<FriendSuggestionDto>(suggestions.Count);
        foreach (var (user, mutualCount) in suggestions)
        {
            var avatar = await BuildAvatarUrlAsync(user.AvatarObjectKey, user.AvatarUrl, ct);
            dtos.Add(new FriendSuggestionDto(
                user.Id, user.Username, user.DisplayName, user.Initials, user.Color, avatar, mutualCount));
        }

        return Result<IReadOnlyList<FriendSuggestionDto>>.Ok(dtos);
    }

    public async Task<Result<DiscoveryResultDto>> DiscoverByUsernameAsync(
        Guid actorId, string username, CancellationToken ct = default)
    {
        var normalized = username.Trim().ToUpperInvariant();
        var user = await _users.GetByNormalizedUsernameAsync(normalized, ct);

        // Do the same membership/visibility work whether or not the account exists so an attacker cannot
        // enumerate accounts by response timing (verified end-to-end on real Postgres in the test slice).
        var probeId = user?.Id ?? Guid.Empty;
        var suspended = user is not null && user.IsAccountSuspended();
        var blocked = await _friends.IsBlockedInEitherDirectionAsync(actorId, probeId, ct);
        var settings = await _friends.GetSettingsAsync(probeId, ct);
        var privacy = settings?.FriendRequestPrivacy ?? FriendRequestPrivacy.Anyone;
        var mutual = await _friends.GetMutualFriendCountAsync(actorId, probeId, ct);

        var eligible = user is not null
            && user.Id != actorId
            && !suspended
            && !blocked
            && user.Visibility != ProfileVisibility.Private
            && privacy != FriendRequestPrivacy.Off
            && (privacy != FriendRequestPrivacy.FriendsOfFriends || mutual > 0);

        if (!eligible)
            return NotVisible<DiscoveryResultDto>();

        var avatar = await BuildAvatarUrlAsync(user!.AvatarObjectKey, user.AvatarUrl, ct);
        return Result<DiscoveryResultDto>.Ok(new DiscoveryResultDto(
            user.Id, user.Username, user.DisplayName, user.Initials, user.Color, avatar));
    }

    public async Task<Result> DismissSuggestionAsync(
        Guid actorId, Guid suggestedUserId, CancellationToken ct = default)
    {
        if (actorId == suggestedUserId)
            return Result.Fail(NotVisibleCode, "The requested resource was not found.");

        var target = await _users.GetByIdAsync(suggestedUserId, ct);
        var blocked = await _friends.IsBlockedInEitherDirectionAsync(actorId, suggestedUserId, ct);
        if (target is null || target.IsAccountSuspended() || blocked
            || target.Visibility == ProfileVisibility.Private)
            return Result.Fail(NotVisibleCode, "The requested resource was not found.");

        await _friends.UpsertDismissalAsync(actorId, suggestedUserId, DateTime.UtcNow + DismissalWindow, ct);
        return Result.Ok();   // idempotent 204
    }

    // ── Block / Unblock ───────────────────────────────────────────────────────────

    public async Task<Result<BlockUserResult>> BlockUserAsync(
        Guid actorId, Guid targetUserId, CancellationToken ct = default)
    {
        if (actorId == targetUserId)
            return Result<BlockUserResult>.Fail(SelfBlock, "You cannot block yourself.");

        var target = await _users.GetByIdAsync(targetUserId, ct);
        if (target is null)
            return NotVisible<BlockUserResult>();

        var existing = await _friends.GetBlockAsync(actorId, targetUserId, ct);
        if (existing is not null)
            return Result<BlockUserResult>.Ok(new BlockUserResult(
                BlockUserResult.AlreadyBlocked, targetUserId, existing.CreatedAt));

        // Atomically end any pending/accepted edge and insert the block.
        var edge = await _friends.GetEdgeAsync(actorId, targetUserId, ct);
        Friendship? endedEdge = null;
        Domain.Outbox.OutboxMessage? removedEvent = null;
        if (edge is not null && edge.Status is FriendshipStatus.Pending or FriendshipStatus.Accepted)
        {
            var wasAccepted = edge.Status == FriendshipStatus.Accepted;
            edge.EndByBlock(actorId);
            endedEdge = edge;
            if (wasAccepted) removedEvent = FriendOutbox.FriendshipRemovedEvent(edge);
        }

        var block = Block.Create(actorId, targetUserId);
        var outcome = await _friends.BlockAndCancelFriendshipAsync(
            block, endedEdge, FriendOutbox.UserBlockedEvent(block), removedEvent, ct);

        if (outcome == AddBlockOutcome.Conflict)   // racing duplicate block → idempotent 200
            return Result<BlockUserResult>.Ok(new BlockUserResult(BlockUserResult.AlreadyBlocked, targetUserId, DateTime.UtcNow));
        if (outcome == AddBlockOutcome.ConcurrencyConflict)
            return Result<BlockUserResult>.Fail(ConcurrencyConflict, "A concurrent operation modified this relationship. Please retry.");

        _logger.LogInformation("Security: User blocked. ActorId={ActorId} TargetId={TargetId}", actorId, targetUserId);
        return Result<BlockUserResult>.Ok(new BlockUserResult(BlockUserResult.Blocked, targetUserId, block.CreatedAt));
    }

    public async Task<Result> UnblockUserAsync(
        Guid actorId, Guid blockedUserId, CancellationToken ct = default)
    {
        var block = await _friends.GetBlockAsync(actorId, blockedUserId, ct);
        if (block is null)
            return Result.Ok();   // idempotent 204 — unblocking restores nothing

        await _friends.RemoveBlockAsync(block, FriendOutbox.UserUnblockedEvent(block), ct);
        _logger.LogInformation("Security: User unblocked. ActorId={ActorId} TargetId={TargetId}", actorId, blockedUserId);
        return Result.Ok();
    }

    // ── Settings ──────────────────────────────────────────────────────────────────

    public async Task<Result<FriendSettingsDto>> GetSettingsAsync(Guid actorId, CancellationToken ct = default)
    {
        var settings = await _friends.GetSettingsAsync(actorId, ct);
        var privacy = settings?.FriendRequestPrivacy ?? FriendRequestPrivacy.Anyone;
        return Result<FriendSettingsDto>.Ok(new FriendSettingsDto(privacy.ToString()));
    }

    public async Task<Result<FriendSettingsDto>> UpdateSettingsAsync(
        Guid actorId, string friendRequestPrivacy, CancellationToken ct = default)
    {
        if (!Enum.TryParse<FriendRequestPrivacy>(friendRequestPrivacy, ignoreCase: true, out var privacy))
            return Result<FriendSettingsDto>.Fail(
                ValidationFailed, "FriendRequestPrivacy must be one of: Anyone, FriendsOfFriends, Off.");

        await _friends.UpsertSettingsAsync(actorId, privacy, ct);
        return Result<FriendSettingsDto>.Ok(new FriendSettingsDto(privacy.ToString()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static int ClampLimit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    private static Result<T> NotVisible<T>() =>
        Result<T>.Fail(NotVisibleCode, "The requested resource was not found.");

    /// <summary>Blank query → all friends (null); otherwise 2–100 normalized chars (leading @ stripped).</summary>
    private static string? NormalizeFriendQuery(string? query, out string? error)
    {
        error = null;
        if (query is null) return null;
        query = query.Trim();
        if (query.StartsWith('@')) query = query[1..];
        if (query.Length == 0) return null;
        if (query.Length < 2 || query.Length > 100)
        {
            error = "Search query must be between 2 and 100 characters.";
            return null;
        }
        return query.ToUpperInvariant();
    }

    private async Task<Result<SendFriendRequestResult>> SendResultAsync(
        Friendship f, User actor, User target, string outcome, CancellationToken ct)
    {
        var mutual = await _friends.GetMutualFriendCountAsync(f.RequesterId, f.AddresseeId, ct);
        var (requester, addressee) = f.RequesterId == actor.Id ? (actor, target) : (target, actor);
        var dto = await BuildRequestDtoAsync(f, requester, addressee, mutual, ct);
        return Result<SendFriendRequestResult>.Ok(new SendFriendRequestResult(outcome, dto));
    }

    private async Task<Result<FriendRequestDto>> RequestDtoAsync(Friendship f, CancellationToken ct)
    {
        var requester = await _users.GetByIdAsync(f.RequesterId, ct);
        var addressee = await _users.GetByIdAsync(f.AddresseeId, ct);
        var mutual = await _friends.GetMutualFriendCountAsync(f.RequesterId, f.AddresseeId, ct);
        return Result<FriendRequestDto>.Ok(await BuildRequestDtoAsync(f, requester!, addressee!, mutual, ct));
    }

    private async Task<FriendRequestDto> BuildRequestDtoAsync(
        Friendship f, User requester, User addressee, int mutualCount, CancellationToken ct)
    {
        var reqAvatar = await BuildAvatarUrlAsync(requester.AvatarObjectKey, requester.AvatarUrl, ct);
        var addrAvatar = await BuildAvatarUrlAsync(addressee.AvatarObjectKey, addressee.AvatarUrl, ct);
        return new FriendRequestDto(
            f.Id, f.RequesterId,
            requester.Username, requester.DisplayName, requester.Initials, requester.Color, reqAvatar,
            f.AddresseeId,
            addressee.Username, addressee.DisplayName, addressee.Initials, addressee.Color, addrAvatar,
            f.Status.ToString(), f.SentAt, mutualCount);
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
}
