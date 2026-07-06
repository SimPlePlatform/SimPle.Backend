namespace SimPle.Application.Friends.DTOs;

public sealed record FriendRequestDto(
    Guid RequestId,
    Guid RequesterId,
    string RequesterUsername,
    string RequesterDisplayName,
    string RequesterInitials,
    string RequesterColor,
    string? RequesterAvatarUrl,
    Guid AddresseeId,
    string AddresseeUsername,
    string AddresseeDisplayName,
    string AddresseeInitials,
    string AddresseeColor,
    string? AddresseeAvatarUrl,
    string Status,
    DateTime RequestedAt,       // ← Friendship.SentAt; never expose CreatedAt
    int MutualFriendCount);
