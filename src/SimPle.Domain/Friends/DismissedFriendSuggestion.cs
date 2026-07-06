using SimPle.Domain.Common;

namespace SimPle.Domain.Friends;

/// <summary>
/// Suppresses a friend suggestion for the dismissing user for a bounded window (30 days) or until a
/// relationship forms, whichever is first. Composite-unique on (UserId, SuggestedUserId). Expired rows may
/// be deleted by the cleanup job without affecting any relationship state.
/// </summary>
public class DismissedFriendSuggestion : Entity
{
    public Guid UserId { get; private set; }
    public Guid SuggestedUserId { get; private set; }
    public DateTime DismissedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    private DismissedFriendSuggestion() { }

    public static DismissedFriendSuggestion Create(Guid userId, Guid suggestedUserId, DateTime expiresAt)
    {
        var now = DateTime.UtcNow;
        return new DismissedFriendSuggestion
        {
            UserId = userId,
            SuggestedUserId = suggestedUserId,
            DismissedAt = now,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Refresh an existing dismissal (idempotent repeat) to a new 30-day window.</summary>
    public void Renew(DateTime expiresAt)
    {
        DismissedAt = DateTime.UtcNow;
        ExpiresAt = expiresAt;
        Touch();
    }
}
