namespace SimPle.Domain.GameHost;

public enum SeatOutcome
{
    Win = 0,
    Loss = 1,
    Draw = 2,
}

/// <param name="Seat">Zero-based seat index this outcome belongs to.</param>
/// <param name="Score">Engine-defined final score. Not a rating — Module 10 owns rating and Elo.</param>
public readonly record struct SeatResult(int Seat, SeatOutcome Outcome, int Score);

/// <summary>
/// The engine's verdict on a terminal state. It is a <b>candidate</b>, not a recorded result: Module 5 is a
/// pure function and claims nothing about "exactly once". Module 8 persists the unique terminal result row and
/// its outbox event; consumers deliver at-least-once and deduplicate.
/// <para>
/// Because <c>EvaluateResult</c> is pure, asking a terminal state for its result twice returns the same
/// candidate — replay is safe by construction rather than by a guard.
/// </para>
/// </summary>
public sealed class TerminalResultCandidate
{
    public IReadOnlyList<SeatResult> SeatResults { get; }

    private TerminalResultCandidate(IReadOnlyList<SeatResult> seatResults) => SeatResults = seatResults;

    public static TerminalResultCandidate Create(IEnumerable<SeatResult> seatResults)
    {
        var results = seatResults.ToList();
        if (results.Count == 0)
            throw new ArgumentException("A terminal result must cover at least one seat.", nameof(seatResults));

        if (results.Select(r => r.Seat).Distinct().Count() != results.Count)
            throw new ArgumentException("A seat may not appear twice in a terminal result.", nameof(seatResults));

        return new TerminalResultCandidate(results);
    }

    public bool IsDraw => SeatResults.All(r => r.Outcome == SeatOutcome.Draw);
}
