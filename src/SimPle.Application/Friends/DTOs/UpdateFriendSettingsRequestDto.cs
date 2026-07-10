namespace SimPle.Application.Friends.DTOs;

public sealed record UpdateFriendSettingsRequestDto(
    string FriendRequestPrivacy,
    string? SearchVisibility = null,
    string? FriendsListVisibility = null);
