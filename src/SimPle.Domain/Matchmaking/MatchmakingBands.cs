namespace SimPle.Domain.Matchmaking;

/// <summary>
/// The Phase-1 rating bands, by ticket age. Monotonically widening (±100 → ±200 → ±400) is half the
/// anti-starvation guarantee; anchoring proposals on the oldest ticket (6C) is the other half. A waiting ticket's
/// band only ever grows, so it is never indefinitely skipped while the queue serves easier matches — and the
/// absolute 60-second deadline bounds the worst case, making <em>expiry</em>, not silent starvation, the terminal
/// outcome.
///
/// The boundaries are half-open by construction: <c>[0s,15s) → ±100</c>, <c>[15s,30s) → ±200</c>,
/// <c>[30s,60s) → ±400</c>, <c>≥60s → expired</c>. Exactly 15s is already ±200, exactly 60s is already expired.
/// The brief (Risk #8) makes fake-clock coverage of these three instants mandatory precisely because an
/// off-by-one here is invisible to a wall-clock test.
/// </summary>
public static class MatchmakingBands
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan SecondBandAt = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ThirdBandAt = TimeSpan.FromSeconds(30);

    public const int NarrowBand = 100;
    public const int MediumBand = 200;
    public const int WideBand = 400;

    /// <summary>
    /// The half-width of the ticket's rating band at the given age. Returns null once the ticket has reached its
    /// deadline — an expired ticket has no band, it has a terminal outcome.
    /// </summary>
    public static int? BandFor(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(age), "Ticket age must not be negative.");

        if (age >= Deadline) return null;
        if (age >= ThirdBandAt) return WideBand;
        if (age >= SecondBandAt) return MediumBand;
        return NarrowBand;
    }
}
