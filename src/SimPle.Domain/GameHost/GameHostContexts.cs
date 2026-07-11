namespace SimPle.Domain.GameHost;

/// <summary>
/// One seat at the table, bound by Module 8 from authenticated lobby membership. A game definition reads
/// seats; it never authenticates or authorizes anyone.
/// </summary>
/// <param name="Seat">Zero-based seat index. Stable for the life of the match.</param>
/// <param name="UserId">The account occupying the seat, or <see langword="null"/> for an AI seat.</param>
/// <param name="IsAi">Whether Module 9 drives this seat.</param>
public readonly record struct SeatAssignment(int Seat, Guid? UserId, bool IsAi);

/// <summary>
/// Everything a definition needs to build its opening state. The seed is <b>not</b> here: it reaches the
/// definition only as a live <see cref="Pcg32"/> stream, so a definition cannot read, store, or echo the raw
/// seed value even by accident.
/// </summary>
public sealed class GameSetup
{
    public IReadOnlyList<SeatAssignment> Seats { get; }

    /// <summary>The catalog mode this match is being played in (e.g. <c>multiplayer</c>, <c>solo</c>).</summary>
    public string Mode { get; }

    private GameSetup(IReadOnlyList<SeatAssignment> seats, string mode)
    {
        Seats = seats;
        Mode = mode;
    }

    public static GameSetup Create(IEnumerable<SeatAssignment> seats, string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("Mode must not be empty.", nameof(mode));

        var list = seats.ToList();
        if (list.Count is < EngineLimits.MinPlayers or > EngineLimits.MaxPlayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seats),
                list.Count,
                $"Seat count must be between {EngineLimits.MinPlayers} and {EngineLimits.MaxPlayers}.");
        }

        // Seats are the definition's only notion of "who": a gap or duplicate would let a command target an
        // ambiguous seat, so the shape is validated before any engine code sees it.
        var expected = Enumerable.Range(0, list.Count).ToHashSet();
        if (!list.Select(s => s.Seat).ToHashSet().SetEquals(expected))
            throw new ArgumentException("Seats must be a contiguous zero-based range with no duplicates.", nameof(seats));

        var humanIds = list.Where(s => !s.IsAi && s.UserId is not null).Select(s => s.UserId!.Value).ToList();
        if (humanIds.Distinct().Count() != humanIds.Count)
            throw new ArgumentException("A user may not occupy two seats in the same match.", nameof(seats));

        return new GameSetup(list, mode);
    }
}

/// <summary>
/// The server's account of who is issuing a command. Every field is bound by Module 8 from authenticated
/// membership — a definition must treat these as authoritative and must never read an actor identity out of
/// the command payload, which is client-supplied.
/// </summary>
/// <param name="CommandId">Client-generated idempotency key. Module 8 stores the receipt; the engine only echoes it.</param>
/// <param name="ActorUserId">Server-bound account issuing the command.</param>
/// <param name="ActorSeat">Server-bound seat of that account.</param>
/// <param name="ExpectedRevision">The revision the caller believes it is acting on.</param>
/// <param name="CurrentRevision">The revision the authoritative state is actually at.</param>
public readonly record struct CommandContext(
    Guid CommandId,
    Guid ActorUserId,
    int ActorSeat,
    int ExpectedRevision,
    int CurrentRevision)
{
    /// <summary>A command issued against a revision that is no longer current must be rejected, never applied.</summary>
    public bool IsStale => ExpectedRevision != CurrentRevision;
}

public enum ViewerRole
{
    Player = 0,
    Spectator = 1,
}

/// <summary>
/// Who a projection is being built for. A spectator has no seat and is entitled to no private data: there is
/// no default full-state serialization to fall back on, so a definition must build the spectator projection
/// explicitly or expose nothing.
/// </summary>
public readonly record struct ViewerContext(ViewerRole Role, int? Seat, Guid? UserId)
{
    public static ViewerContext ForPlayer(int seat, Guid userId) => new(ViewerRole.Player, seat, userId);

    public static ViewerContext ForSpectator(Guid? userId = null) => new(ViewerRole.Spectator, null, userId);

    public bool IsSpectator => Role == ViewerRole.Spectator;
}
