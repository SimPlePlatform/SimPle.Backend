namespace SimPle.Shared.Common;

/// <summary>
/// Shared minimal identity for any surface that lists another account (search, friend/mutual drill-down,
/// suggestions). Later modules compose their own owned fields around this; they must never clone an
/// incompatible identity shape. Never carries bio, social links, region, status, ELO, level, or presence.
/// </summary>
public sealed record PublicIdentityDto(
    Guid UserId,
    string Username,
    string DisplayName,
    string Initials,
    string Color,
    string? AvatarUrl,
    string ProfileType);
