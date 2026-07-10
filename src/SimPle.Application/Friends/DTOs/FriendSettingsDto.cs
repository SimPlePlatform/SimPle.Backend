namespace SimPle.Application.Friends.DTOs;

public sealed record FriendSettingsDto(
    string FriendRequestPrivacy,
    string SearchVisibility,
    string FriendsListVisibility);
