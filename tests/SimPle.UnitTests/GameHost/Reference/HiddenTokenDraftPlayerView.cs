namespace SimPle.UnitTests.GameHost.Reference;

/// <summary>
/// The redacted projection for <see cref="HiddenTokenDraftDefinition"/>. Turn/count state is public; only
/// <see cref="OwnHand"/> carries hidden information, and it is populated only for the seat it belongs to — a
/// spectator and every other seat always see it as <see langword="null"/>.
/// </summary>
public sealed class HiddenTokenDraftPlayerView
{
    public int SeatCount { get; set; }

    public int CurrentSeat { get; set; }

    public int ConsecutivePasses { get; set; }

    public int TokensRemaining { get; set; }

    /// <summary>Public hand sizes, index-aligned with seat number. Reveals count, never contents.</summary>
    public List<int> HandSizes { get; set; } = new();

    /// <summary>The requesting seat's own hand contents, or <see langword="null"/> for a spectator or any other seat.</summary>
    public List<int>? OwnHand { get; set; }
}
