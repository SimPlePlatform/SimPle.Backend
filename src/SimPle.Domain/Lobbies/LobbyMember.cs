using SimPle.Domain.Common;

namespace SimPle.Domain.Lobbies;

/// <summary>
/// A seat in a lobby. Child of <see cref="Lobby"/>; constructible and mutable only through the owning aggregate,
/// which is what keeps the readiness-reset and host-transfer rules in one place.
///
/// <see cref="JoinedAtUtc"/> is the tenure clock that drives deterministic host transfer. It is supplied by the
/// caller's injected <c>TimeProvider</c>, never read from <see cref="Entity.CreatedAt"/> — that base-class field
/// is populated by a raw <c>DateTime.UtcNow</c> the module deliberately does not refactor (R4), so it is not
/// fake-clock controllable and must never carry a time-sensitive rule.
/// </summary>
public class LobbyMember : Entity
{
    public Guid LobbyId { get; private set; }
    public Guid UserId { get; private set; }
    public LobbyMemberState State { get; private set; } = LobbyMemberState.Joined;

    /// <summary>Readiness is a separate boolean, never a member state. The host is implicitly ready.</summary>
    public bool IsReady { get; private set; }

    public DateTime JoinedAtUtc { get; private set; }
    public DateTime? LeftAtUtc { get; private set; }

    /// <summary>The host who kicked this member. Null for a voluntary leave.</summary>
    public Guid? RemovedByUserId { get; private set; }

    private LobbyMember() { }

    internal static LobbyMember Join(Guid lobbyId, Guid userId, DateTime joinedAtUtc, bool isReady) => new()
    {
        LobbyId = lobbyId,
        UserId = userId,
        State = LobbyMemberState.Joined,
        IsReady = isReady,
        JoinedAtUtc = joinedAtUtc,
    };

    internal bool IsJoined => State == LobbyMemberState.Joined;

    internal void SetReadiness(bool isReady)
    {
        IsReady = isReady;
        Touch();
    }

    internal void Leave(DateTime leftAtUtc)
    {
        State = LobbyMemberState.Left;
        IsReady = false;
        LeftAtUtc = leftAtUtc;
        Touch();
    }

    internal void Kick(Guid removedByUserId, DateTime kickedAtUtc)
    {
        State = LobbyMemberState.Kicked;
        IsReady = false;
        LeftAtUtc = kickedAtUtc;
        RemovedByUserId = removedByUserId;
        Touch();
    }
}
