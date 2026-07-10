namespace SimPle.Application.Profiles.DTOs;

public sealed record ProfileDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string? Bio,
    string? AvatarUrl,
    string? BannerUrl,
    bool HasUploadedAvatar,
    bool HasUploadedBanner,
    string? StatusMessage,
    string Region,
    string Color,
    string BannerFallbackColor,
    string Initials,
    string Visibility,   // "Public" | "FriendsOnly" | "Private"
    string ProfileType,  // "Player" | "Developer"
    string Role,
    int Level,
    int Elo,
    int FriendCount,
    DateTime JoinedAt,
    IReadOnlyList<ExternalLinkDto> Links,
    IReadOnlyList<string> Interests);

public sealed record ExternalLinkDto(
    Guid Id,
    string Platform,
    string Url,
    string? DisplayLabel,
    int SortOrder);
