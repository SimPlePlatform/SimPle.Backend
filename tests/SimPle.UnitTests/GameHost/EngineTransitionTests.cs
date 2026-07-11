using System.Text;
using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// The central safety property of a rejection: it must be indistinguishable from a command that was never
/// issued. No next state, no events, no revision bump — and therefore, at the host, no RNG advance. A partially
/// applied rejection would leak a mutated board or a drawn card through a move the rules refused.
/// </summary>
public sealed class EngineTransitionTests
{
    private sealed class FakeState
    {
        public int Turn { get; init; }
    }

    private static GameStateEnvelope Envelope(int revision) => GameStateEnvelope.Create(
        gameSlug: "hidden-token-draft",
        engineVersion: 1,
        stateSchemaVersion: 1,
        revision: revision,
        rngState: Pcg32.FromSeedParts(42UL, 54UL).Snapshot(),
        stateBytes: Encoding.UTF8.GetBytes($$"""{"turn":{{revision}}}"""));

    // ── EngineTransition (non-generic, the shape Module 8 consumes) ──────────────────────────────────────

    [Fact]
    public void Reject_CarriesNoStateNoEventsAndDoesNotAdvanceTheRevision()
    {
        var transition = EngineTransition.Reject(priorRevision: 7, EngineErrorCode.IllegalActor, "not your turn");

        transition.Accepted.Should().BeFalse();
        transition.NextState.Should().BeNull("a rejection must never hand back state, hidden or otherwise");
        transition.PublicEvents.Should().BeEmpty();
        transition.PrivateEvents.Should().BeEmpty();
        transition.NextRevision.Should().Be(7, "a rejected command leaves the match exactly where it was");
        transition.PriorRevision.Should().Be(7);
        transition.TerminalResult.Should().BeNull();
        transition.RejectionCode.Should().Be("Engine.IllegalActor");
        transition.RejectionDetail.Should().Be("not your turn");
    }

    [Fact]
    public void Accept_AdvancesTheRevisionByExactlyOne()
    {
        var transition = EngineTransition.Accept(priorRevision: 3, Envelope(4), [], terminalResult: null);

        transition.Accepted.Should().BeTrue();
        transition.PriorRevision.Should().Be(3);
        transition.NextRevision.Should().Be(4);
        transition.NextState.Should().NotBeNull();
        transition.EngineState.Should().Be(EngineState.InProgress);
    }

    [Fact]
    public void Accept_WithAStateThatSkipsARevision_Throws()
    {
        // A revision that jumps is either a lost command or a replayed one. Both are bugs the host must not
        // paper over, because the revision is what makes stale-command detection work at all.
        var accept = () => EngineTransition.Accept(priorRevision: 3, Envelope(5), [], terminalResult: null);

        accept.Should().Throw<ArgumentException>().WithMessage("*exactly one*");
    }

    [Fact]
    public void Accept_PartitionsEventsIntoPublicAndPrivateBatches()
    {
        // Visibility is decided by the definition, not the transport. Getting this partition wrong is precisely
        // how a hidden card ends up broadcast to the table.
        var events = new[]
        {
            GameEvent.Public("TurnEnded", 1),
            GameEvent.Private("TokenDrawn", 1, targetSeat: 2),
            GameEvent.Public("CountChanged", 1),
        };

        var transition = EngineTransition.Accept(priorRevision: 0, Envelope(1), events, terminalResult: null);

        transition.PublicEvents.Should().HaveCount(2);
        transition.PrivateEvents.Should().ContainSingle()
            .Which.TargetSeat.Should().Be(2);
    }

    [Fact]
    public void Accept_WithATerminalResult_ReportsTerminalEngineState()
    {
        var result = TerminalResultCandidate.Create([
            new SeatResult(0, SeatOutcome.Win, 10),
            new SeatResult(1, SeatOutcome.Loss, 4),
        ]);

        var transition = EngineTransition.Accept(priorRevision: 8, Envelope(9), [], result);

        transition.EngineState.Should().Be(EngineState.Terminal);
        transition.TerminalResult.Should().BeSameAs(result);
    }

    [Fact]
    public void Accept_WithMoreThanTheEventCap_Throws()
    {
        var tooMany = Enumerable
            .Range(0, EngineLimits.MaxEmittedEventsPerCommand + 1)
            .Select(_ => GameEvent.Public("Noise", 1))
            .ToList();

        var accept = () => EngineTransition.Accept(priorRevision: 0, Envelope(1), tooMany, terminalResult: null);

        accept.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── EngineDecision<TState> (typed, what a definition returns) ────────────────────────────────────────

    [Fact]
    public void Decision_Reject_CarriesNoNextStateAndNoEvents()
    {
        var decision = EngineDecision<FakeState>.Reject(EngineErrorCode.InvalidCommand, "no such move");

        decision.Accepted.Should().BeFalse();
        decision.NextState.Should().BeNull();
        decision.Events.Should().BeEmpty();
        decision.TerminalResult.Should().BeNull();
        decision.RejectionCode.Should().Be(EngineErrorCode.InvalidCommand);
        decision.EngineState.Should().Be(EngineState.InProgress);
    }

    [Fact]
    public void Decision_Accept_CarriesTheNewStateAndInfersEngineState()
    {
        var decision = EngineDecision<FakeState>.Accept(new FakeState { Turn = 1 });

        decision.Accepted.Should().BeTrue();
        decision.NextState!.Turn.Should().Be(1);
        decision.EngineState.Should().Be(EngineState.InProgress);
    }

    [Fact]
    public void Decision_AcceptWithTerminalResult_InfersTerminalEngineState()
    {
        var result = TerminalResultCandidate.Create([new SeatResult(0, SeatOutcome.Draw, 0)]);

        var decision = EngineDecision<FakeState>.Accept(new FakeState { Turn = 9 }, terminalResult: result);

        decision.EngineState.Should().Be(EngineState.Terminal);
        decision.TerminalResult.Should().BeSameAs(result);
    }

    [Fact]
    public void Decision_AcceptWithMoreThanTheEventCap_Throws()
    {
        var tooMany = Enumerable
            .Range(0, EngineLimits.MaxEmittedEventsPerCommand + 1)
            .Select(_ => GameEvent.Public("Noise", 1))
            .ToList();

        var accept = () => EngineDecision<FakeState>.Accept(new FakeState(), tooMany);

        accept.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── TerminalResultCandidate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TerminalResult_WithADuplicateSeat_Throws()
    {
        var create = () => TerminalResultCandidate.Create([
            new SeatResult(0, SeatOutcome.Win, 10),
            new SeatResult(0, SeatOutcome.Loss, 2),
        ]);

        create.Should().Throw<ArgumentException>().WithMessage("*twice*");
    }

    [Fact]
    public void TerminalResult_WithNoSeats_Throws()
    {
        var create = () => TerminalResultCandidate.Create([]);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TerminalResult_IsDraw_OnlyWhenEverySeatDrew()
    {
        var drawn = TerminalResultCandidate.Create([
            new SeatResult(0, SeatOutcome.Draw, 5),
            new SeatResult(1, SeatOutcome.Draw, 5),
        ]);

        var decided = TerminalResultCandidate.Create([
            new SeatResult(0, SeatOutcome.Win, 6),
            new SeatResult(1, SeatOutcome.Draw, 5),
        ]);

        drawn.IsDraw.Should().BeTrue();
        decided.IsDraw.Should().BeFalse();
    }
}
