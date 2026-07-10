using SimPle.Domain.Common;

namespace SimPle.Domain.Profiles;

/// <summary>
/// Marks a normalized username as permanently non-reassignable once its owner renames away from it, so an
/// old `/u/{name}` link can never later resolve to a different identity. Deliberately has no FK relationship
/// to <c>User</c>: the retired value must survive the prior owner's account deletion (account deletion
/// preserves only this minimum non-reassignable value, never a full identity record).
/// </summary>
public class RetiredUsername : Entity
{
    public string NormalizedUsername { get; private set; } = default!;
    public Guid PriorOwnerUserId { get; private set; }
    public DateTime RetiredAtUtc { get; private set; }

    private RetiredUsername() { }

    public static RetiredUsername Create(string normalizedUsername, Guid priorOwnerUserId) => new()
    {
        NormalizedUsername = normalizedUsername,
        PriorOwnerUserId = priorOwnerUserId,
        RetiredAtUtc = DateTime.UtcNow,
    };
}
