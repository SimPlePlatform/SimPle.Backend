namespace SimPle.Application.Friends.DTOs;

/// <summary>
/// Outcome discriminator for POST /api/friends/blocks. The controller maps <see cref="Outcome"/> to the HTTP
/// status: <c>blocked</c> → 201, <c>already_blocked</c> → 200. Deliberately carries no target identity fields
/// (M03-007): the caller already has the identity card from whatever surface initiated the block, and unlike
/// <c>GET /api/friends/blocks</c> (the caller's own list) this path does not gate on the target's visibility.
/// </summary>
public sealed record BlockUserResult(string Outcome, Guid BlockedUserId, DateTime BlockedAt)
{
    public const string Blocked = "blocked";
    public const string AlreadyBlocked = "already_blocked";
}
