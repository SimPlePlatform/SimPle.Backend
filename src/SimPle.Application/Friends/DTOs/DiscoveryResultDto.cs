namespace SimPle.Application.Friends.DTOs;

/// <summary>
/// Minimal identity returned by safe exact-username discovery. Never carries bio, social links, region,
/// status, ELO, level, mutual identities, email, or presence. An ineligible target (private / Off /
/// blocked / deleted / suspended / banned / nonexistent) yields an identical 404 instead of this DTO.
/// </summary>
public sealed record DiscoveryResultDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string Initials,
    string Color,
    string? AvatarUrl);
