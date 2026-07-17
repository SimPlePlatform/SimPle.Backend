using System.Collections.Concurrent;

namespace SimPle.Api.Realtime;

/// <summary>
/// Maps a user id to their live hub connections' abort actions. SignalR's <c>IHubContext</c> can message a user
/// (<c>Clients.User(id)</c>) but cannot forcibly close their connection — only <c>HubCallerContext.Abort()</c>,
/// called from inside the hub instance that owns that connection, can. This singleton is the bridge:
/// <c>RealtimeHub</c> registers each connection's abort delegate on connect and removes it on disconnect;
/// <c>RealtimeConnectionCloser</c> (the real, SignalR-backed <c>IRealtimeConnectionCloser</c>) invokes them by
/// user id from anywhere in the app (e.g. <c>AuthService.LogoutAsync</c>).
/// </summary>
public sealed class RealtimeConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Action>> _byUser = new();

    public void Register(Guid userId, string connectionId, Action abort)
    {
        var connections = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, Action>());
        connections[connectionId] = abort;
    }

    public void Unregister(Guid userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
            if (connections.IsEmpty)
                _byUser.TryRemove(userId, out _);
        }
    }

    /// <summary>Aborts every live connection currently registered for this user. Never throws for a user with
    /// no connections.</summary>
    public void AbortAll(Guid userId)
    {
        if (!_byUser.TryRemove(userId, out var connections))
            return;

        foreach (var abort in connections.Values)
            abort();
    }
}
