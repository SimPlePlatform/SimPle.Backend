using SimPle.Application.Common.Interfaces;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Realtime.Contracts;
using SimPle.Domain.Lobbies;

namespace SimPle.Application.Realtime.Authorization;

/// <summary>
/// Authorizes lobby-scope realtime actions, mirroring <c>LobbiesService.GetAsync</c>'s exact privacy rule
/// (docs/specs/module-07-realtime-presence-chat-spec.md, "Authorization / Privacy rules"): existence is never
/// disclosed, so every denial collapses to the same <see cref="LobbyErrors.NotFound"/> code already used by the
/// REST API — a distinct "forbidden" code here would itself leak that the lobby exists.
///
/// Called on every hub method that touches the scope, never only at connect/subscribe time — the "load-bearing
/// rule": a cached principal from handshake time is not trusted for authorization, only for identity.
/// </summary>
public sealed class LobbyScopeAuthorizer : IRealtimeScopeAuthorizer
{
    private readonly ILobbyRepository _lobbies;
    private readonly IUserRepository _users;
    private readonly IFriendRepository _friends;

    public LobbyScopeAuthorizer(ILobbyRepository lobbies, IUserRepository users, IFriendRepository friends)
    {
        _lobbies = lobbies;
        _users = users;
        _friends = friends;
    }

    public string ScopeKind => RealtimeEnvelope.LobbyScope;

    public async Task<RealtimeScopeAuthorizationResult> AuthorizeAsync(
        Guid actorUserId, Guid scopeId, RealtimeAction action, CancellationToken ct = default)
    {
        var lobby = await _lobbies.GetByIdAsync(scopeId, ct);
        if (lobby is null)
            return Deny();

        var isMember = lobby.FindJoinedMember(actorUserId) is not null;
        var isPubliclyVisible = lobby.Privacy == LobbyPrivacy.Public && lobby.State == LobbyState.Open;

        // Subscribing (read-only) allows public-and-open lobbies even for non-members, matching the REST
        // GetAsync visibility rule exactly. Send/Delete always require membership regardless of visibility —
        // an anonymous observer of a public lobby is never allowed to act inside it.
        var visibilityAllows = action == RealtimeAction.Subscribe ? isMember || isPubliclyVisible : isMember;
        if (!visibilityAllows)
            return Deny();

        var actor = await _users.GetByIdAsync(actorUserId, ct);
        if (actor is null || actor.IsAccountSuspended())
            return Deny();

        // Block check: only meaningful against the host (the only other party guaranteed to matter for every
        // lobby, public or private). Self-guard avoids a false positive when the actor is the host.
        if (lobby.HostUserId != actorUserId)
        {
            var blocked = await _friends.IsBlockedInEitherDirectionAsync(actorUserId, lobby.HostUserId, ct);
            if (blocked)
                return Deny();
        }

        return RealtimeScopeAuthorizationResult.Allow();
    }

    private static RealtimeScopeAuthorizationResult Deny() =>
        RealtimeScopeAuthorizationResult.Deny(LobbyErrors.NotFound);
}
