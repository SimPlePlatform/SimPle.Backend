using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.Reference;

/// <summary>
/// Test-only reference engine used to exercise the Module 5 host machinery end-to-end: 2-4 seats, a
/// <c>PCG32-v1</c>-driven shared token pool, private hands, public turn/count state, <c>draw</c>/<c>pass</c>
/// commands, a deterministic terminal score, and a spectator projection.
/// <para>
/// <b>Never registered in production DI or inserted into the Module 4 catalog.</b> It lives under
/// <c>tests/SimPle.UnitTests/GameHost/Reference</c> specifically so no production composition root can reach it
/// by accident — only test projects reference this assembly.
/// </para>
/// </summary>
public sealed class HiddenTokenDraftDefinition
    : IGameDefinition<HiddenTokenDraftState, HiddenTokenDraftCommand, HiddenTokenDraftPlayerView>
{
    public const string Slug = "hidden-token-draft-reference";

    internal const int TokensPerSeat = 6;

    public GameDefinitionMetadata Metadata { get; } = GameDefinitionMetadata.Create(
        slug: Slug,
        engineVersion: 1,
        stateSchemaVersion: 1,
        minPlayers: 2,
        maxPlayers: 4,
        supportedModes: new[] { "multiplayer" },
        hasHiddenInformation: true,
        supportsSpectatorView: true,
        supportsAi: false,
        supportsTimer: false,
        supportsRanked: false,
        supportsDeterministicReplay: true);

    public HiddenTokenDraftState CreateInitialState(GameSetup setup, Pcg32 rng, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var seatCount = setup.Seats.Count;
        var deck = new List<int>(seatCount * TokensPerSeat);
        for (var token = 1; token <= seatCount * TokensPerSeat; token++)
            deck.Add(token);

        var hands = new List<List<int>>(seatCount);
        for (var seat = 0; seat < seatCount; seat++)
            hands.Add(new List<int>());

        return new HiddenTokenDraftState
        {
            SeatCount = seatCount,
            CurrentSeat = 0,
            ConsecutivePasses = 0,
            DeckTokens = deck,
            Hands = hands,
        };
    }

    public EngineDecision<HiddenTokenDraftState> ApplyCommand(
        HiddenTokenDraftState state,
        HiddenTokenDraftCommand command,
        CommandContext context,
        Pcg32 rng,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsTerminal(state))
            return EngineDecision<HiddenTokenDraftState>.Reject(EngineErrorCode.InvalidCommand, "Match is already terminal.");

        if (context.ActorSeat < 0 || context.ActorSeat >= state.SeatCount)
            return EngineDecision<HiddenTokenDraftState>.Reject(EngineErrorCode.IllegalActor, "Seat is out of range.");

        if (context.ActorSeat != state.CurrentSeat)
            return EngineDecision<HiddenTokenDraftState>.Reject(EngineErrorCode.IllegalActor, "It is not this seat's turn.");

        return command switch
        {
            DrawTokenCommand => ApplyDraw(state, rng),
            PassTurnCommand => ApplyPass(state),
            _ => EngineDecision<HiddenTokenDraftState>.Reject(EngineErrorCode.InvalidCommand, "Unrecognized command."),
        };
    }

    public HiddenTokenDraftPlayerView ProjectView(HiddenTokenDraftState state, ViewerContext viewer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var handSizes = state.Hands.Select(hand => hand.Count).ToList();

        List<int>? ownHand = null;
        if (!viewer.IsSpectator)
        {
            var seat = viewer.Seat!.Value;
            if (seat < 0 || seat >= state.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(viewer), seat, "Seat is out of range for this match.");

            ownHand = new List<int>(state.Hands[seat]);
        }

        return new HiddenTokenDraftPlayerView
        {
            SeatCount = state.SeatCount,
            CurrentSeat = state.CurrentSeat,
            ConsecutivePasses = state.ConsecutivePasses,
            TokensRemaining = state.DeckTokens.Count,
            HandSizes = handSizes,
            OwnHand = ownHand,
        };
    }

    public TerminalResultCandidate? EvaluateResult(HiddenTokenDraftState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsTerminal(state) ? BuildTerminalResult(state) : null;
    }

    private static EngineDecision<HiddenTokenDraftState> ApplyDraw(HiddenTokenDraftState state, Pcg32 rng)
    {
        if (state.DeckTokens.Count == 0)
            return EngineDecision<HiddenTokenDraftState>.Reject(EngineErrorCode.InvalidCommand, "Deck is empty.");

        var index = (int)rng.NextBounded((uint)state.DeckTokens.Count);
        var token = state.DeckTokens[index];

        var nextDeck = new List<int>(state.DeckTokens);
        nextDeck.RemoveAt(index);

        var nextHands = CopyHands(state.Hands);
        nextHands[state.CurrentSeat].Add(token);

        var nextState = new HiddenTokenDraftState
        {
            SeatCount = state.SeatCount,
            CurrentSeat = (state.CurrentSeat + 1) % state.SeatCount,
            ConsecutivePasses = 0,
            DeckTokens = nextDeck,
            Hands = nextHands,
        };

        var events = new[] { GameEvent.Public("TokenDrawn", schemaVersion: 1) };
        var terminal = IsTerminal(nextState) ? BuildTerminalResult(nextState) : null;

        return EngineDecision<HiddenTokenDraftState>.Accept(nextState, events, terminal);
    }

    private static EngineDecision<HiddenTokenDraftState> ApplyPass(HiddenTokenDraftState state)
    {
        var nextState = new HiddenTokenDraftState
        {
            SeatCount = state.SeatCount,
            CurrentSeat = (state.CurrentSeat + 1) % state.SeatCount,
            ConsecutivePasses = state.ConsecutivePasses + 1,
            DeckTokens = new List<int>(state.DeckTokens),
            Hands = CopyHands(state.Hands),
        };

        var events = new[] { GameEvent.Public("TurnPassed", schemaVersion: 1) };
        var terminal = IsTerminal(nextState) ? BuildTerminalResult(nextState) : null;

        return EngineDecision<HiddenTokenDraftState>.Accept(nextState, events, terminal);
    }

    private static List<List<int>> CopyHands(List<List<int>> hands) =>
        hands.Select(hand => new List<int>(hand)).ToList();

    private static bool IsTerminal(HiddenTokenDraftState state) =>
        state.DeckTokens.Count == 0 || state.ConsecutivePasses >= state.SeatCount;

    private static TerminalResultCandidate BuildTerminalResult(HiddenTokenDraftState state)
    {
        var scores = state.Hands.Select(hand => hand.Sum()).ToList();
        var topScore = scores.Max();
        var winners = scores.Count(score => score == topScore);

        var results = new List<SeatResult>(state.SeatCount);
        for (var seat = 0; seat < state.SeatCount; seat++)
        {
            var outcome = scores[seat] != topScore
                ? SeatOutcome.Loss
                : winners > 1 ? SeatOutcome.Draw : SeatOutcome.Win;
            results.Add(new SeatResult(seat, outcome, scores[seat]));
        }

        return TerminalResultCandidate.Create(results);
    }
}
