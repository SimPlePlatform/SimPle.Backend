namespace SimPle.Application.People.DTOs;

/// <summary>
/// <see cref="SimPle.Shared.Common.PublicIdentityDto"/> plus viewer-relative search context. Never carries
/// bio, social links, region, status, ELO, level, or presence.
/// </summary>
public sealed record PeopleSearchResultDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string Initials,
    string Color,
    string? AvatarUrl,
    string ProfileType,
    int VisibleMutualFriendCount,
    string RelationshipState);
