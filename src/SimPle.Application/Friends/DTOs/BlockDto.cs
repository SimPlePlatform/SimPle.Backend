namespace SimPle.Application.Friends.DTOs;

public sealed record BlockDto(
    Guid BlockedUserId,
    string BlockedUsername,
    string BlockedDisplayName,
    string BlockedInitials,
    string BlockedColor,
    string? BlockedAvatarUrl,
    DateTime BlockedAt);
