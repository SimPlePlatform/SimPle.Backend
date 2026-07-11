using FluentAssertions;
using SimPle.Domain.GameHost;

namespace SimPle.UnitTests.GameHost;

/// <summary>
/// Seats are the definition's only notion of "who". If the seat set could contain a gap, a duplicate, or the
/// same user twice, a command would be able to target an ambiguous seat — so the shape is validated before any
/// engine code sees it.
/// </summary>
public sealed class GameHostContextsTests
{
    private static SeatAssignment Human(int seat) => new(seat, Guid.NewGuid(), IsAi: false);

    [Fact]
    public void GameSetup_WithAContiguousSeatRange_Succeeds()
    {
        var setup = GameSetup.Create([Human(0), Human(1), Human(2)], "multiplayer");

        setup.Seats.Should().HaveCount(3);
        setup.Mode.Should().Be("multiplayer");
    }

    [Fact]
    public void GameSetup_WithASeatGap_Throws()
    {
        var setup = () => GameSetup.Create([Human(0), Human(2)], "multiplayer");

        setup.Should().Throw<ArgumentException>().WithMessage("*contiguous*");
    }

    [Fact]
    public void GameSetup_WithADuplicateSeat_Throws()
    {
        var setup = () => GameSetup.Create([Human(0), Human(0)], "multiplayer");

        setup.Should().Throw<ArgumentException>().WithMessage("*contiguous*");
    }

    [Fact]
    public void GameSetup_WithTheSameUserInTwoSeats_Throws()
    {
        // Otherwise one account could act for two seats and see both hands — a hidden-information break dressed
        // up as a seating mistake.
        var userId = Guid.NewGuid();

        var setup = () => GameSetup.Create(
            [new SeatAssignment(0, userId, false), new SeatAssignment(1, userId, false)],
            "multiplayer");

        setup.Should().Throw<ArgumentException>().WithMessage("*two seats*");
    }

    [Fact]
    public void GameSetup_AllowsMultipleAiSeatsWithNoUserId()
    {
        var setup = GameSetup.Create(
            [Human(0), new SeatAssignment(1, null, true), new SeatAssignment(2, null, true)],
            "ai");

        setup.Seats.Count(s => s.IsAi).Should().Be(2);
    }

    [Fact]
    public void GameSetup_WithNoSeats_Throws()
    {
        var setup = () => GameSetup.Create([], "solo");

        setup.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GameSetup_WithMoreThanEightSeats_Throws()
    {
        var seats = Enumerable.Range(0, 9).Select(Human).ToList();

        var setup = () => GameSetup.Create(seats, "multiplayer");

        setup.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CommandContext_IsStale_WhenTheExpectedRevisionIsNotCurrent()
    {
        var current = new CommandContext(Guid.NewGuid(), Guid.NewGuid(), 0, ExpectedRevision: 4, CurrentRevision: 4);
        var stale = new CommandContext(Guid.NewGuid(), Guid.NewGuid(), 0, ExpectedRevision: 3, CurrentRevision: 4);

        current.IsStale.Should().BeFalse();
        stale.IsStale.Should().BeTrue();
    }

    [Fact]
    public void ViewerContext_ForSpectator_HasNoSeat()
    {
        var spectator = ViewerContext.ForSpectator();

        spectator.IsSpectator.Should().BeTrue();
        spectator.Seat.Should().BeNull("a spectator holds no seat and is entitled to no seat-private data");
    }

    [Fact]
    public void ViewerContext_ForPlayer_CarriesTheSeatAndAccount()
    {
        var userId = Guid.NewGuid();

        var player = ViewerContext.ForPlayer(seat: 2, userId: userId);

        player.IsSpectator.Should().BeFalse();
        player.Seat.Should().Be(2);
        player.UserId.Should().Be(userId);
    }
}
