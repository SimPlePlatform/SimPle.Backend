using SimPle.Domain.Common;

namespace SimPle.Domain.Games;

/// <summary>Child row of <see cref="Game"/>; constructible only via the owning <see cref="Game"/>.</summary>
public class GameModeCapability : Entity
{
    public Guid GameId { get; private set; }
    public string Mode { get; private set; } = default!;

    private GameModeCapability() { }

    internal static GameModeCapability Create(Guid gameId, string mode) => new()
    {
        GameId = gameId,
        Mode = mode,
    };
}
