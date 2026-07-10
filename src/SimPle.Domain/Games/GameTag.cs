using SimPle.Domain.Common;

namespace SimPle.Domain.Games;

/// <summary>Child row of <see cref="Game"/>; constructible only via the owning <see cref="Game"/>.</summary>
public class GameTag : Entity
{
    public Guid GameId { get; private set; }
    public string Value { get; private set; } = default!;

    private GameTag() { }

    internal static GameTag Create(Guid gameId, string value) => new()
    {
        GameId = gameId,
        Value = value,
    };
}
