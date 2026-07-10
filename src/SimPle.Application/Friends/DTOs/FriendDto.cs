namespace SimPle.Application.Friends.DTOs;

// Safe fields only. level/elo were removed until M10 supplies authoritative ratings (reconciliation R5).
public sealed record FriendDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string Initials,
    string Color,
    string? AvatarUrl,
    DateTime FriendsSince);
