using Microsoft.AspNetCore.SignalR;
using SimPle.Api.Hubs;
using SimPle.Application.Realtime.Contracts;

namespace SimPle.Api.Realtime;

/// <summary>
/// Real, SignalR-backed <see cref="IRealtimeNotifier"/>. Lives in the API layer (not Infrastructure) because it
/// needs the concrete <see cref="RealtimeHub"/> type via <see cref="IHubContext{THub,T}"/>, and Infrastructure
/// cannot reference Api in this codebase's Clean Architecture layering (Api references Infrastructure, never the
/// reverse) — a deliberate, documented deviation from the literal "implementations live in Infrastructure"
/// instruction (see final report). No caller invokes these methods yet in B1 (no chat, no lobby command wiring);
/// they exist now so B2 needs zero new hub/client contract surface.
/// </summary>
public sealed class RealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<RealtimeHub, IRealtimeClient> _hub;
    private readonly TimeProvider _timeProvider;

    public RealtimeNotifier(IHubContext<RealtimeHub, IRealtimeClient> hub, TimeProvider timeProvider)
    {
        _hub = hub;
        _timeProvider = timeProvider;
    }

    public async Task NotifyLobbyChangedAsync(
        Guid lobbyId, int revision, string changeType, CancellationToken ct = default)
    {
        var envelope = RealtimeEnvelope.ForLobby(lobbyId, Now());
        await _hub.Clients.Group(RealtimeGroups.Lobby(lobbyId)).LobbyChanged(envelope, revision, changeType);
    }

    public async Task NotifyPresenceChangedAsync(
        Guid subjectUserId, IReadOnlyCollection<Guid> viewerUserIds, string status, Guid serverEpoch,
        long userVersion, CancellationToken ct = default)
    {
        if (viewerUserIds.Count == 0)
            return;

        var envelope = RealtimeEnvelope.ForUser(subjectUserId, Now());
        await _hub.Clients.Users(viewerUserIds.Select(id => id.ToString()).ToList())
            .PresenceChanged(envelope, subjectUserId, status, serverEpoch, userVersion);
    }

    public async Task NotifyChatMessageCreatedAsync(
        Guid lobbyId, IReadOnlyCollection<Guid> recipientUserIds, ChatMessageDto message,
        CancellationToken ct = default)
    {
        if (recipientUserIds.Count == 0)
            return;

        var envelope = RealtimeEnvelope.ForLobby(lobbyId, Now());
        await _hub.Clients.Users(recipientUserIds.Select(id => id.ToString()).ToList())
            .ChatMessageCreated(envelope, message);
    }

    public async Task NotifyChatMessageDeletedAsync(
        Guid lobbyId, IReadOnlyCollection<Guid> recipientUserIds, Guid messageId, DateTime deletedAtUtc,
        CancellationToken ct = default)
    {
        if (recipientUserIds.Count == 0)
            return;

        var envelope = RealtimeEnvelope.ForLobby(lobbyId, Now());
        await _hub.Clients.Users(recipientUserIds.Select(id => id.ToString()).ToList())
            .ChatMessageDeleted(envelope, messageId, deletedAtUtc);
    }

    public async Task NotifyAccessRevokedAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        var envelope = RealtimeEnvelope.ForUser(userId, Now());
        await _hub.Clients.User(userId.ToString()).AccessRevoked(envelope, reason);
    }

    public async Task NotifyResyncRequiredAsync(
        Guid lobbyId, string reason, int? currentRevision, CancellationToken ct = default)
    {
        var envelope = RealtimeEnvelope.ForLobby(lobbyId, Now());
        await _hub.Clients.Group(RealtimeGroups.Lobby(lobbyId)).ResyncRequired(envelope, reason, currentRevision);
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
