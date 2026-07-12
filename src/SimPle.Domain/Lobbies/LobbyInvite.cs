using SimPle.Domain.Common;

namespace SimPle.Domain.Lobbies;

/// <summary>
/// A targeted invitation. <c>Pending -&gt; Accepted|Revoked|Expired</c>.
///
/// An invite is <em>not</em> a membership: a user may hold many pending invites while holding at most one joined
/// lobby or one nonterminal ticket, and an unsolicited invite never blocks them from joining or queueing
/// elsewhere. Accepting an invite is what creates a <see cref="LobbyMember"/>.
/// </summary>
public class LobbyInvite : Entity
{
    /// <summary>A targeted invite expires 30 minutes after it is sent, or when the lobby closes/starts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public Guid LobbyId { get; private set; }
    public Guid InviterUserId { get; private set; }
    public Guid InviteeUserId { get; private set; }
    public LobbyInviteState State { get; private set; } = LobbyInviteState.Pending;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RespondedAtUtc { get; private set; }

    private LobbyInvite() { }

    public static LobbyInvite Create(Guid lobbyId, Guid inviterUserId, Guid inviteeUserId, DateTime nowUtc)
    {
        if (inviterUserId == inviteeUserId)
            throw new ArgumentException("A user cannot invite themselves.", nameof(inviteeUserId));

        return new LobbyInvite
        {
            LobbyId = lobbyId,
            InviterUserId = inviterUserId,
            InviteeUserId = inviteeUserId,
            State = LobbyInviteState.Pending,
            ExpiresAtUtc = nowUtc + Lifetime,
        };
    }

    public bool IsPending => State == LobbyInviteState.Pending;

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>
    /// Redeemable only while pending and unexpired. Redeeming does <em>not</em> extend the deadline — the invite is
    /// consumed, not refreshed.
    /// </summary>
    public bool CanAccept(DateTime nowUtc) => IsPending && !IsExpired(nowUtc);

    public LobbyOutcome Accept(DateTime nowUtc)
    {
        if (!IsPending) return LobbyOutcome.Closed;
        if (IsExpired(nowUtc)) return LobbyOutcome.Expired;

        State = LobbyInviteState.Accepted;
        RespondedAtUtc = nowUtc;
        Touch();
        return LobbyOutcome.Ok;
    }

    public LobbyOutcome Revoke(DateTime nowUtc)
    {
        if (!IsPending) return LobbyOutcome.Closed;

        State = LobbyInviteState.Revoked;
        RespondedAtUtc = nowUtc;
        Touch();
        return LobbyOutcome.Ok;
    }

    /// <summary>Expiry sweep entry point. Idempotent — a re-run over an already-terminal invite is a no-op.</summary>
    public bool TryExpire(DateTime nowUtc)
    {
        if (!IsPending || !IsExpired(nowUtc)) return false;

        State = LobbyInviteState.Expired;
        RespondedAtUtc = nowUtc;
        Touch();
        return true;
    }
}
