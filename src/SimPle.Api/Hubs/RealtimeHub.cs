using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SimPle.Api.Realtime;
using SimPle.Application.Chat;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Realtime;
using SimPle.Application.Realtime.Authorization;
using SimPle.Application.Realtime.Contracts;
using SimPle.Application.Realtime.Presence;

namespace SimPle.Api.Hubs;

/// <summary>
/// The single authenticated realtime endpoint (docs/specs/module-07-realtime-presence-chat-spec.md). Reuses the
/// existing HttpOnly <c>access_token</c> cookie JWT auth already wired in <c>Program.cs</c>
/// (<c>OnMessageReceived</c>/<c>OnTokenValidated</c>) — no separate auth path for realtime.
///
/// <c>SendLobbyMessage</c> (added in backend session B, M07-B2) delegates entirely to <see cref="IChatService"/>:
/// idempotency, rate limiting, normalization, profanity screening, persistence, and fan-out all live there, not
/// in this file — the hub method is a thin authenticated transport shim, same as every other method here.
///
/// Every method that touches a scope re-authorizes against current owner data — a cached principal from
/// handshake time is never trusted alone for authorization (see "the load-bearing rule" in the spec). Groups are
/// a delivery optimization only, never an authorization mechanism.
/// </summary>
[Authorize]
public sealed class RealtimeHub : Hub<IRealtimeClient>
{
    private readonly IPresenceRegistry _presence;
    private readonly IRealtimeNotifier _notifier;
    private readonly IPresenceViewerResolver _viewerResolver;
    private readonly ILobbyRepository _lobbies;
    private readonly IRealtimeRateLimiter _rateLimiter;
    private readonly RealtimeConnectionTracker _tracker;
    private readonly IReadOnlyDictionary<string, IRealtimeScopeAuthorizer> _authorizers;
    private readonly IChatService _chat;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RealtimeHub> _logger;

    public RealtimeHub(
        IPresenceRegistry presence,
        IRealtimeNotifier notifier,
        IPresenceViewerResolver viewerResolver,
        ILobbyRepository lobbies,
        IRealtimeRateLimiter rateLimiter,
        RealtimeConnectionTracker tracker,
        IEnumerable<IRealtimeScopeAuthorizer> authorizers,
        IChatService chat,
        TimeProvider timeProvider,
        ILogger<RealtimeHub> logger)
    {
        _presence = presence;
        _notifier = notifier;
        _viewerResolver = viewerResolver;
        _lobbies = lobbies;
        _rateLimiter = rateLimiter;
        _tracker = tracker;
        _authorizers = authorizers.ToDictionary(a => a.ScopeKind);
        _chat = chat;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();

        // Captured before any mutation, for the before/after diff below — the registry's own PresenceUpdateResult
        // is only reliable straight off SetLobbyMembership; TryConnect returns a bare bool, and a GetStatus called
        // immediately after it would recompute the *same* status it just set, so its own Changed flag is always
        // false at that point. Comparing this snapshot against a later GetStatus is the only way to detect the
        // real transition.
        var beforeStatus = _presence.GetStatus(userId).Status;

        var connectionAccepted = _rateLimiter.TryAcquireConnection(userId);
        var presenceAccepted = connectionAccepted && _presence.TryConnect(userId, Context.ConnectionId);

        if (!connectionAccepted || !presenceAccepted)
        {
            if (connectionAccepted)
                _rateLimiter.ReleaseConnection(userId);

            _logger.LogInformation(
                "Realtime connection rejected: connection limit reached. UserId={UserId}", userId);
            throw new HubException("Realtime.ConnectionLimit");
        }

        // Capture the HubCallerContext itself, not `this` — the Hub instance is transient and disposed right
        // after this method returns, so a lambda that reads the `Context` property lazily (`() => Context.Abort()`)
        // throws ObjectDisposedException the moment it's invoked from a later, unrelated request.
        var callerContext = Context;
        _tracker.Register(userId, callerContext.ConnectionId, () => callerContext.Abort());

        try
        {
            // Reconnect gap: a lobby member whose connection dropped (network blip, server restart) and now
            // reconnects never actually left the lobby, so they should show InLobby immediately — not wait for a
            // lobby event that isn't coming, since nothing about the lobby itself changed.
            var activeLobby = await _lobbies.GetActiveLobbyForUserAsync(userId, callerContext.ConnectionAborted);
            if (activeLobby is not null)
                _presence.SetLobbyMembership(userId, activeLobby.Id, true);

            await BroadcastPresenceIfChangedAsync(userId, beforeStatus, callerContext.ConnectionAborted);

            var envelope = RealtimeEnvelope.ForUser(userId, Now());
            await Clients.Caller.Connected(envelope, _presence.ServerEpoch);

            await base.OnConnectedAsync();
        }
        catch
        {
            // M07-002: everything acquired above (rate-limit lease, presence entry, tracker registration) must
            // be released on any failure past this point — otherwise a client that connects then drops before
            // the handshake reply completes leaks a permanent slot against its connection/rate limits.
            _tracker.Unregister(userId, callerContext.ConnectionId);
            _rateLimiter.ReleaseConnection(userId);
            _presence.Disconnect(userId, callerContext.ConnectionId);
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var beforeStatus = _presence.GetStatus(userId).Status;

        _tracker.Unregister(userId, Context.ConnectionId);
        _rateLimiter.ReleaseConnection(userId);
        _presence.Disconnect(userId, Context.ConnectionId);

        // CancellationToken.None, not Context.ConnectionAborted: that token belongs to the connection that just
        // died, and is typically already cancelling by the time this runs — it must not cancel a broadcast meant
        // for the *other* users watching this one's presence.
        await BroadcastPresenceIfChangedAsync(userId, beforeStatus, CancellationToken.None);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Privacy-safe: a missing lobby, a private lobby the caller cannot see, and an existing-but-
    /// unauthorized lobby all surface the identical <c>Lobbies.NotFound</c> error — existence is never
    /// disclosed. A lobby-scope authorizer always exists in B1; a request for a scope kind with no registered
    /// authorizer (defensive only — should be unreachable while only "lobby" is ever routed here) also denies via
    /// <see cref="NullMatchScopeAuthorizer.ScopeNotAvailableCode"/>.</summary>
    public async Task<SubscribeLobbyResultDto> SubscribeLobby(Guid lobbyId)
    {
        var userId = GetUserId();
        var result = await AuthorizeAsync(RealtimeEnvelope.LobbyScope, userId, lobbyId, RealtimeAction.Subscribe);
        if (!result.IsAllowed)
            throw new HubException(result.ErrorCode);

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Lobby(lobbyId), Context.ConnectionAborted);

        // Revision is not known here without a repository round trip beyond what the authorizer already did;
        // callers fetch the authoritative snapshot via GET /api/lobbies/{lobbyId} immediately after subscribing
        // (see spec API Contract). 0 signals "fetch the snapshot yourself", never a real revision.
        return new SubscribeLobbyResultDto(0);
    }

    public async Task UnsubscribeLobby(Guid lobbyId)
    {
        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId, RealtimeGroups.Lobby(lobbyId), Context.ConnectionAborted);
    }

    /// <summary>A duplicate <paramref name="clientCommandId"/> returns the original message rather than erroring
    /// or creating a second row — <see cref="IChatService.SendAsync"/>'s idempotency contract. Detailed failure
    /// reasons never reach the client as free text: the <see cref="HubException"/> message is always the stable
    /// <c>ChatErrors</c> code, matching every other authorization failure in this hub.</summary>
    public async Task<SendLobbyMessageResultDto> SendLobbyMessage(Guid lobbyId, string body, Guid clientCommandId)
    {
        var userId = GetUserId();
        var result = await _chat.SendAsync(userId, lobbyId, body, clientCommandId, Context.ConnectionAborted);
        if (!result.IsSuccess)
            throw new HubException(result.Error!.Code);

        return new SendLobbyMessageResultDto(result.Value!);
    }

    /// <summary>Throttled to at most once per 60s per connection at the presence-registry level; a throttled
    /// (rejected) signal is a silent no-op — it is not an error, and it mutates nothing.</summary>
    public async Task ReportActivity()
    {
        var userId = GetUserId();
        var beforeStatus = _presence.GetStatus(userId).Status;

        if (!_presence.TryReportActivity(userId, Context.ConnectionId))
            return;

        await BroadcastPresenceIfChangedAsync(userId, beforeStatus, Context.ConnectionAborted);
    }

    private async Task<RealtimeScopeAuthorizationResult> AuthorizeAsync(
        string scopeKind, Guid userId, Guid scopeId, RealtimeAction action)
    {
        if (!_authorizers.TryGetValue(scopeKind, out var authorizer))
            return RealtimeScopeAuthorizationResult.Deny(NullMatchScopeAuthorizer.ScopeNotAvailableCode);

        return await authorizer.AuthorizeAsync(userId, scopeId, action, Context.ConnectionAborted);
    }

    /// <summary>Broadcasts the caller's current presence only if it actually differs from <paramref
    /// name="beforeStatus"/> — the external diff this hub relies on throughout, since none of TryConnect/
    /// Disconnect/TryReportActivity hand back a trustworthy Changed flag of their own once a second GetStatus call
    /// has already re-observed the same state.</summary>
    private async Task BroadcastPresenceIfChangedAsync(Guid userId, PresenceStatus beforeStatus, CancellationToken ct)
    {
        var after = _presence.GetStatus(userId);
        if (after.Status == beforeStatus)
            return;

        var viewers = await _viewerResolver.ResolveAsync(userId, ct);
        await _notifier.NotifyPresenceChangedAsync(
            userId, viewers, after.Status.ToString(), after.ServerEpoch, after.UserVersion, ct);
    }

    private Guid GetUserId() => Guid.Parse(Context.UserIdentifier!);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
