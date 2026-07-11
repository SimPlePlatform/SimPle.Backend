using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost.Reference;

/// <summary>
/// Exercises <see cref="HiddenTokenDraftDefinition"/> directly at the typed <see cref="IGameDefinition{TState,TCommand,TPlayerView}"/>
/// layer — the properties the whole Module 5 host machinery depends on the reference engine actually having:
/// determinism from a fixed RNG stream, RNG advancing only on accepted draws, hidden-hand isolation in
/// projections, and the two independent terminal conditions (deck exhaustion, a full round of passes).
/// </summary>
public sealed class HiddenTokenDraftDefinitionTests
{
    private readonly HiddenTokenDraftDefinition _definition = new();

    private static readonly Guid Seat0User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Seat1User = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GameSetup TwoSeatSetup() => GameSetup.Create(
        [new SeatAssignment(0, Seat0User, false), new SeatAssignment(1, Seat1User, false)], "multiplayer");

    private static Pcg32 FixedRng() => Pcg32.FromSeedParts(42UL, 7UL);

    private static CommandContext ContextFor(int actorSeat, Guid actorUserId, int revision) =>
        new(Guid.NewGuid(), actorUserId, actorSeat, revision, revision);

    // ── CreateInitialState ─────────────────────────────────────────────────

    [Fact]
    public void CreateInitialState_BuildsAFullDeckAndOneEmptyHandPerSeat()
    {
        var state = _definition.CreateInitialState(TwoSeatSetup(), FixedRng(), CancellationToken.None);

        state.SeatCount.Should().Be(2);
        state.CurrentSeat.Should().Be(0);
        state.ConsecutivePasses.Should().Be(0);
        state.DeckTokens.Should().HaveCount(2 * HiddenTokenDraftDefinition.TokensPerSeat);
        state.DeckTokens.Should().OnlyHaveUniqueItems();
        state.Hands.Should().HaveCount(2);
        state.Hands.Should().OnlyContain(hand => hand.Count == 0);
    }

    [Fact]
    public void CreateInitialState_DoesNotConsumeAnyRngDraws()
    {
        var rng = FixedRng();

        _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        rng.Cursor.Should().Be(0UL, "the opening deck is not shuffled — order is fixed, so setup must not touch the RNG stream");
    }

    // ── RNG advancement ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyCommand_AcceptedDraw_AdvancesTheRngCursorByExactlyOne()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None);

        rng.Cursor.Should().Be(1UL);
    }

    [Fact]
    public void ApplyCommand_AcceptedPass_DoesNotAdvanceTheRngCursor()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        _definition.ApplyCommand(state, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None);

        rng.Cursor.Should().Be(0UL);
    }

    [Fact]
    public void ApplyCommand_RejectedWrongTurn_DoesNotAdvanceTheRngCursor()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        var decision = _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(1, Seat1User, 0), rng, CancellationToken.None);

        decision.Accepted.Should().BeFalse();
        decision.RejectionCode.Should().Be(EngineErrorCode.IllegalActor);
        rng.Cursor.Should().Be(0UL);
    }

    // ── Turn/state transitions ──────────────────────────────────────────────

    [Fact]
    public void ApplyCommand_Draw_MovesOneTokenFromTheDeckIntoTheActingSeatsHand()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var deckSizeBefore = state.DeckTokens.Count;

        var decision = _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        decision.NextState!.DeckTokens.Should().HaveCount(deckSizeBefore - 1);
        decision.NextState.Hands[0].Should().HaveCount(1);
        decision.NextState.Hands[1].Should().BeEmpty();
    }

    [Fact]
    public void ApplyCommand_Draw_AdvancesCurrentSeatRoundRobinAndResetsConsecutivePasses()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var afterPass = _definition.ApplyCommand(state, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None).NextState!;

        var decision = _definition.ApplyCommand(afterPass, new DrawTokenCommand(), ContextFor(1, Seat1User, 0), rng, CancellationToken.None);

        decision.NextState!.CurrentSeat.Should().Be(0);
        decision.NextState.ConsecutivePasses.Should().Be(0);
    }

    [Fact]
    public void ApplyCommand_OnATerminalState_RejectsInvalidCommand()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var afterPass0 = _definition.ApplyCommand(state, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None).NextState!;
        var afterPass1 = _definition.ApplyCommand(afterPass0, new PassTurnCommand(), ContextFor(1, Seat1User, 0), rng, CancellationToken.None).NextState!;

        var decision = _definition.ApplyCommand(afterPass1, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None);

        decision.Accepted.Should().BeFalse();
        decision.RejectionCode.Should().Be(EngineErrorCode.InvalidCommand);
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void SameSeedAndSameCommandSequence_ProducesIdenticalHandsAcrossTwoIndependentRuns()
    {
        var commands = new HiddenTokenDraftCommand[] { new DrawTokenCommand(), new DrawTokenCommand(), new DrawTokenCommand(), new DrawTokenCommand() };

        var handsRunA = PlayFixedSequence(commands);
        var handsRunB = PlayFixedSequence(commands);

        handsRunA.Should().BeEquivalentTo(handsRunB, options => options.WithStrictOrdering());
    }

    private List<List<int>> PlayFixedSequence(IReadOnlyList<HiddenTokenDraftCommand> commands)
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        foreach (var command in commands)
        {
            var actorUser = state.CurrentSeat == 0 ? Seat0User : Seat1User;
            var decision = _definition.ApplyCommand(state, command, ContextFor(state.CurrentSeat, actorUser, 0), rng, CancellationToken.None);
            state = decision.NextState!;
        }

        return state.Hands;
    }

    // ── Projections / hidden-view isolation ────────────────────────────────

    [Fact]
    public void ProjectView_Spectator_HasNoOwnHandButSeesEveryHandSize()
    {
        var rng = FixedRng();
        var state = MidGameStateWithNonEmptyHands(rng);

        var view = _definition.ProjectView(state, ViewerContext.ForSpectator(), CancellationToken.None);

        view.OwnHand.Should().BeNull();
        view.HandSizes.Should().Equal(state.Hands.Select(h => h.Count));
    }

    [Fact]
    public void ProjectView_Player_SeesOnlyItsOwnHandContentsNotAnotherSeats()
    {
        var rng = FixedRng();
        var state = MidGameStateWithNonEmptyHands(rng);

        var viewSeat0 = _definition.ProjectView(state, ViewerContext.ForPlayer(0, Seat0User), CancellationToken.None);
        var viewSeat1 = _definition.ProjectView(state, ViewerContext.ForPlayer(1, Seat1User), CancellationToken.None);

        viewSeat0.OwnHand.Should().Equal(state.Hands[0]);
        viewSeat1.OwnHand.Should().Equal(state.Hands[1]);
        viewSeat0.OwnHand.Should().NotEqual(viewSeat1.OwnHand);
    }

    private HiddenTokenDraftState MidGameStateWithNonEmptyHands(Pcg32 rng)
    {
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var afterSeat0 = _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None).NextState!;
        var afterSeat1 = _definition.ApplyCommand(afterSeat0, new DrawTokenCommand(), ContextFor(1, Seat1User, 0), rng, CancellationToken.None).NextState!;
        return afterSeat1;
    }

    // ── Terminal conditions ──────────────────────────────────────────────────

    [Fact]
    public void EvaluateResult_ReturnsNull_WhileDeckIsNonEmptyAndPassesAreBelowSeatCount()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var afterOnePass = _definition.ApplyCommand(state, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None).NextState!;

        _definition.EvaluateResult(afterOnePass, CancellationToken.None).Should().BeNull();
    }

    [Fact]
    public void EvaluateResult_ATerminal_OnceEverySeatHasPassedConsecutively()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);
        var afterPass0 = _definition.ApplyCommand(state, new PassTurnCommand(), ContextFor(0, Seat0User, 0), rng, CancellationToken.None).NextState!;
        var afterPass1 = _definition.ApplyCommand(afterPass0, new PassTurnCommand(), ContextFor(1, Seat1User, 0), rng, CancellationToken.None).NextState!;

        _definition.EvaluateResult(afterPass1, CancellationToken.None).Should().NotBeNull();
    }

    [Fact]
    public void EvaluateResult_ATerminal_OnceTheDeckIsFullyDrained()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        var totalTokens = state.DeckTokens.Count;
        for (var i = 0; i < totalTokens; i++)
        {
            var actorUser = state.CurrentSeat == 0 ? Seat0User : Seat1User;
            var decision = _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(state.CurrentSeat, actorUser, 0), rng, CancellationToken.None);
            state = decision.NextState!;
        }

        state.DeckTokens.Should().BeEmpty();
        _definition.EvaluateResult(state, CancellationToken.None).Should().NotBeNull();
    }

    [Fact]
    public void TerminalResult_ScoresAreTheSumOfEachSeatsHandAndTheHighestScoreWins()
    {
        var rng = FixedRng();
        var state = _definition.CreateInitialState(TwoSeatSetup(), rng, CancellationToken.None);

        var totalTokens = state.DeckTokens.Count;
        for (var i = 0; i < totalTokens; i++)
        {
            var actorUser = state.CurrentSeat == 0 ? Seat0User : Seat1User;
            var decision = _definition.ApplyCommand(state, new DrawTokenCommand(), ContextFor(state.CurrentSeat, actorUser, 0), rng, CancellationToken.None);
            state = decision.NextState!;
        }

        var terminal = _definition.EvaluateResult(state, CancellationToken.None)!;
        var expectedScores = state.Hands.Select(hand => hand.Sum()).ToList();

        terminal.SeatResults.Should().HaveCount(2);
        foreach (var seatResult in terminal.SeatResults)
            seatResult.Score.Should().Be(expectedScores[seatResult.Seat]);

        var topScore = expectedScores.Max();
        var winnerSeats = terminal.SeatResults.Where(r => r.Outcome == SeatOutcome.Win).Select(r => r.Seat).ToList();
        winnerSeats.Should().OnlyContain(seat => expectedScores[seat] == topScore);
    }
}
