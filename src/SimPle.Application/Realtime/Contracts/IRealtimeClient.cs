namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// The typed client contract for <c>Hub&lt;IRealtimeClient&gt;</c>
/// (docs/specs/module-07-realtime-presence-chat-spec.md, "Server-&gt;client events"). All seven events —
/// including the two chat events — are declared and implemented in backend session A (M07-B1), even though
/// nothing sends <see cref="ChatMessageCreated"/>/<see cref="ChatMessageDeleted"/> yet: this is the one place B1
/// does work it does not consume, so that B2 (chat persistence) adds zero new hub/client contract surface.
///
/// Every event carries a <see cref="RealtimeEnvelope"/>. Delivery is at-least-once, never exactly-once — callers
/// must dedupe (chat by message id, presence by (serverEpoch, userVersion), lobby hints by revision).
/// </summary>
public interface IRealtimeClient
{
    /// <summary>Sent from <c>OnConnectedAsync</c>. This is where the client learns the server epoch.</summary>
    Task Connected(RealtimeEnvelope envelope, Guid serverEpoch);

    /// <summary>A thin hint carrying only a revision — never lobby state. Three events at one revision collapse
    /// into exactly one of these.</summary>
    Task LobbyChanged(RealtimeEnvelope envelope, int revision, string changeType);

    /// <summary>Dedupe by (serverEpoch, userVersion); a changed epoch clears all cached presence.</summary>
    Task PresenceChanged(RealtimeEnvelope envelope, Guid userId, string status, Guid serverEpoch, long userVersion);

    /// <summary>Not sent by anything in B1 — chat does not exist yet. Declared for B2.</summary>
    Task ChatMessageCreated(RealtimeEnvelope envelope, ChatMessageDto message);

    /// <summary>Not sent by anything in B1 — chat does not exist yet. Declared for B2.</summary>
    Task ChatMessageDeleted(RealtimeEnvelope envelope, Guid messageId, DateTime deletedAtUtc);

    /// <summary>
    /// Reasons: <c>lobby.membership_removed</c> | <c>lobby.closed</c> | <c>auth.session_revoked</c> |
    /// <c>auth.suspended</c> | <c>social.blocked</c>.
    /// </summary>
    Task AccessRevoked(RealtimeEnvelope envelope, string reason);

    /// <summary>Exactly two reasons: <c>gap</c>, <c>slow_consumer</c>.</summary>
    Task ResyncRequired(RealtimeEnvelope envelope, string reason, int? currentRevision);
}
