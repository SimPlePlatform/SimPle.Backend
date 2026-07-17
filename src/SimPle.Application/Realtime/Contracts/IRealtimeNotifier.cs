namespace SimPle.Application.Realtime.Contracts;

/// <summary>
/// Server-side fan-out abstraction wrapping <see cref="IRealtimeClient"/> plus envelope construction and
/// group/user targeting. Declared and implemented in B1 (the concrete SignalR-backed implementation lives in the
/// API layer, since it needs the concrete hub type) but has no caller yet — B2's outbox handler
/// (<c>LobbyRealtimeHandler</c>) and chat commands are what will actually invoke these methods. Shipping this now
/// with its final shape is what keeps B2 from needing to touch the hub/client contract at all.
/// </summary>
public interface IRealtimeNotifier
{
    Task NotifyLobbyChangedAsync(Guid lobbyId, int revision, string changeType, CancellationToken ct = default);

    Task NotifyPresenceChangedAsync(
        Guid subjectUserId, IReadOnlyCollection<Guid> viewerUserIds, string status, Guid serverEpoch,
        long userVersion, CancellationToken ct = default);

    /// <summary>Delivered only to <paramref name="recipientUserIds"/>, not the whole lobby group — the caller
    /// (<c>ChatService</c>) has already excluded any member blocked (either direction) with the sender, so a
    /// group broadcast here would bypass that filtering. The sender is always included, for their own other
    /// connections.</summary>
    Task NotifyChatMessageCreatedAsync(
        Guid lobbyId, IReadOnlyCollection<Guid> recipientUserIds, ChatMessageDto message,
        CancellationToken ct = default);

    /// <summary>See <see cref="NotifyChatMessageCreatedAsync"/> — same block-aware recipient filtering
    /// applies to delete/tombstone fan-out.</summary>
    Task NotifyChatMessageDeletedAsync(
        Guid lobbyId, IReadOnlyCollection<Guid> recipientUserIds, Guid messageId, DateTime deletedAtUtc,
        CancellationToken ct = default);

    /// <summary>Sends <c>AccessRevoked</c> to the user's connections. Does not close them — pair with
    /// <c>IRealtimeConnectionCloser</c> for an actual proactive close.</summary>
    Task NotifyAccessRevokedAsync(Guid userId, string reason, CancellationToken ct = default);

    Task NotifyResyncRequiredAsync(
        Guid lobbyId, string reason, int? currentRevision, CancellationToken ct = default);
}
