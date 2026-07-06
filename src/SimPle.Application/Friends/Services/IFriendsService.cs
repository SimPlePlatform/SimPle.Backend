using SimPle.Application.Friends.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.Friends.Services;

public interface IFriendsService
{
    Task<Result<FriendSummaryDto>> GetSummaryAsync(Guid actorId, CancellationToken ct = default);

    Task<Result<CursorPage<FriendDto>>> GetFriendsAsync(
        Guid actorId, string? query, int limit, string? cursor, CancellationToken ct = default);

    Task<Result<CursorPage<FriendRequestDto>>> GetRequestsAsync(
        Guid actorId, string direction, int limit, string? cursor, CancellationToken ct = default);

    Task<Result<SendFriendRequestResult>> SendFriendRequestAsync(
        Guid actorId, Guid targetUserId, CancellationToken ct = default);

    Task<Result<FriendRequestDto>> AcceptFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default);

    Task<Result<FriendRequestDto>> DeclineFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default);

    Task<Result> CancelFriendRequestAsync(
        Guid actorId, Guid requestId, CancellationToken ct = default);

    Task<Result> RemoveFriendAsync(
        Guid actorId, Guid friendUserId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<FriendSuggestionDto>>> GetSuggestionsAsync(
        Guid actorId, int limit, CancellationToken ct = default);

    Task<Result<DiscoveryResultDto>> DiscoverByUsernameAsync(
        Guid actorId, string username, CancellationToken ct = default);

    Task<Result> DismissSuggestionAsync(
        Guid actorId, Guid suggestedUserId, CancellationToken ct = default);

    Task<Result<CursorPage<BlockDto>>> GetBlocksAsync(
        Guid actorId, int limit, string? cursor, CancellationToken ct = default);

    Task<Result<BlockUserResult>> BlockUserAsync(
        Guid actorId, Guid targetUserId, CancellationToken ct = default);

    Task<Result> UnblockUserAsync(
        Guid actorId, Guid blockedUserId, CancellationToken ct = default);

    Task<Result<FriendSettingsDto>> GetSettingsAsync(
        Guid actorId, CancellationToken ct = default);

    Task<Result<FriendSettingsDto>> UpdateSettingsAsync(
        Guid actorId, string friendRequestPrivacy, CancellationToken ct = default);
}
