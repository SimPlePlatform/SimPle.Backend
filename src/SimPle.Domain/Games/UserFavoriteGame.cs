using SimPle.Domain.Common;

namespace SimPle.Domain.Games;

/// <summary>
/// A user's favorite marker for a game. <see cref="CycleId"/> increments on each fresh
/// favorite -&gt; unfavorite -&gt; favorite cycle, giving the future (4B) outbox event a fresh
/// AggregateDomainVersion per cycle. No 4A code creates these rows via an API — schema/domain only.
/// </summary>
public class UserFavoriteGame : Entity
{
    public Guid UserId { get; private set; }
    public Guid GameId { get; private set; }
    public bool IsActive { get; private set; }
    public int CycleId { get; private set; }

    private UserFavoriteGame() { }

    public static UserFavoriteGame Favorite(Guid userId, Guid gameId) => new()
    {
        UserId = userId,
        GameId = gameId,
        IsActive = true,
        CycleId = 1,
    };

    /// <summary>Idempotent: a no-op if already inactive. Does not increment CycleId.</summary>
    public void Unfavorite()
    {
        if (!IsActive) return;
        IsActive = false;
        Touch();
    }

    /// <summary>Idempotent: a no-op if already active. Increments CycleId for a fresh cycle.</summary>
    public void Refavorite()
    {
        if (IsActive) return;
        IsActive = true;
        CycleId += 1;
        Touch();
    }
}
