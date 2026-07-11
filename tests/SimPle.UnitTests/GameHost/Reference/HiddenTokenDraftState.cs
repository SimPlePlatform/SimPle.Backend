namespace SimPle.UnitTests.GameHost.Reference;

/// <summary>
/// Authoritative state for the test-only <see cref="HiddenTokenDraftDefinition"/> reference engine. Plain
/// settable properties (not a record) so the pinned <c>GameHostJsonContext</c> options deserialize it by simple
/// property assignment, with no constructor-parameter-name matching involved.
/// <para>
/// Immutable by convention only: every <see cref="HiddenTokenDraftDefinition"/> method that receives a state
/// builds a new instance rather than mutating this one, per <c>IGameDefinition</c>'s purity contract.
/// </para>
/// </summary>
public sealed class HiddenTokenDraftState
{
    public int SeatCount { get; set; }

    public int CurrentSeat { get; set; }

    /// <summary>Consecutive accepted <c>pass</c> commands since the last accepted <c>draw</c>.</summary>
    public int ConsecutivePasses { get; set; }

    /// <summary>
    /// Remaining tokens in the shared pool. Draw order is decided lazily: each accepted draw removes one
    /// unbiased random element via <c>Pcg32.NextBounded</c>, which is the incremental form of a Fisher-Yates
    /// shuffle — equivalent to shuffling the whole deck up front, but the order past the next draw is never
    /// materialized (or serialized) before it is actually needed.
    /// </summary>
    public List<int> DeckTokens { get; set; } = new();

    /// <summary>One hand per seat, index-aligned with seat number. Holds every seat's hidden information.</summary>
    public List<List<int>> Hands { get; set; } = new();
}
