namespace SimPle.Application.Friends.DTOs;

// Safe fields only. level/elo were removed until M10 (reconciliation R5). MutualFriendCount counts only
// accepted relationships visible to the viewer and never exposes hidden identities.
public sealed record FriendSuggestionDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string Initials,
    string Color,
    string? AvatarUrl,
    int MutualFriendCount);
