namespace SimPle.Application.Realtime.Presence;

/// <summary>
/// In-memory presence tracking (docs/specs/module-07-realtime-presence-chat-spec.md, "Domain Invariants").
/// Single-instance only — no cross-instance sync (see spec Risk Register; a future multi-instance deployment
/// needs a backplane, out of scope for B1). Every computation is lazy: there is no background timer, status is
/// derived from timestamps against the injected <see cref="TimeProvider"/> at call time (test convention mirrors
/// <c>tests/SimPle.UnitTests/Matchmaking/ExpirySweeperTests.cs</c>'s <c>FakeTimeProvider</c> usage).
/// </summary>
public sealed class PresenceRegistry : IPresenceRegistry
{
    internal const int MaxConnectionsPerUser = 5;
    internal static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ActivityThrottle = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan OfflineDebounce = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider;
    private readonly Guid _serverEpoch = Guid.NewGuid();
    private readonly object _lock = new();
    private readonly Dictionary<Guid, UserState> _users = new();

    public PresenceRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Guid ServerEpoch => _serverEpoch;

    public bool TryConnect(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            var state = GetOrCreate(userId);
            if (!state.Connections.ContainsKey(connectionId) && state.Connections.Count >= MaxConnectionsPerUser)
                return false;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            state.Connections[connectionId] = now;
            state.DisconnectedAtUtc = null;
            Recompute(state, now);
            return true;
        }
    }

    public void Disconnect(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var state))
                return;

            state.Connections.Remove(connectionId);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (state.Connections.Count == 0)
                state.DisconnectedAtUtc = now;

            Recompute(state, now);
            EvictIfExpired(userId, state, now);
        }
    }

    public bool TryReportActivity(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var state) ||
                !state.Connections.TryGetValue(connectionId, out var lastActivity))
                return false;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now - lastActivity < ActivityThrottle)
                return false;

            state.Connections[connectionId] = now;
            Recompute(state, now);
            return true;
        }
    }

    public PresenceUpdateResult SetLobbyMembership(Guid userId, Guid lobbyId, bool isMember)
    {
        lock (_lock)
        {
            var state = GetOrCreate(userId);
            if (isMember)
                state.MemberOfLobbyIds.Add(lobbyId);
            else
                state.MemberOfLobbyIds.Remove(lobbyId);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var result = Recompute(state, now);
            EvictIfExpired(userId, state, now);
            return result;
        }
    }

    public PresenceUpdateResult GetStatus(Guid userId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var state))
                return new PresenceUpdateResult(false, PresenceStatus.Offline, _serverEpoch, 0);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var result = Recompute(state, now);
            EvictIfExpired(userId, state, now);
            return result;
        }
    }

    private UserState GetOrCreate(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var state))
        {
            state = new UserState();
            _users[userId] = state;
        }
        return state;
    }

    private PresenceUpdateResult Recompute(UserState state, DateTime now)
    {
        var status = ComputeStatus(state, now);
        var changed = status != state.LastStatus;
        if (changed)
        {
            state.LastStatus = status;
            state.UserVersion++;
        }
        return new PresenceUpdateResult(changed, status, _serverEpoch, state.UserVersion);
    }

    private static PresenceStatus ComputeStatus(UserState state, DateTime now)
    {
        PresenceStatus baseStatus;
        if (state.Connections.Count > 0)
        {
            baseStatus = PresenceStatus.Offline;
            foreach (var lastActivity in state.Connections.Values)
            {
                var connectionStatus = now - lastActivity >= AwayThreshold
                    ? PresenceStatus.Away
                    : PresenceStatus.Online;
                if (connectionStatus > baseStatus)
                    baseStatus = connectionStatus;
            }

            state.LastAliveBase = baseStatus;
        }
        else if (state.DisconnectedAtUtc is { } disconnectedAt && now - disconnectedAt < OfflineDebounce)
        {
            // Debounce grace: preserve the last known live status rather than flapping to Offline immediately
            // (e.g. a page refresh reconnects within a second or two).
            baseStatus = state.LastAliveBase;
        }
        else
        {
            baseStatus = PresenceStatus.Offline;
        }

        // InLobby is a separate axis driven only by MemberOfLobbyIds (never by SubscribedLobbyIds, which drives
        // fan-out only) — and only overlays a genuinely-connected/grace-period user, never a truly Offline one.
        if (baseStatus != PresenceStatus.Offline &&
            state.MemberOfLobbyIds.Count > 0 &&
            baseStatus < PresenceStatus.InLobby)
        {
            baseStatus = PresenceStatus.InLobby;
        }

        return baseStatus;
    }

    private void EvictIfExpired(Guid userId, UserState state, DateTime now)
    {
        if (state.Connections.Count == 0 &&
            state.DisconnectedAtUtc is { } disconnectedAt &&
            now - disconnectedAt >= OfflineDebounce)
        {
            _users.Remove(userId);
        }
    }

    private sealed class UserState
    {
        public Dictionary<string, DateTime> Connections { get; } = new();
        public HashSet<Guid> MemberOfLobbyIds { get; } = new();
        public DateTime? DisconnectedAtUtc { get; set; }
        public PresenceStatus LastAliveBase { get; set; } = PresenceStatus.Offline;
        public PresenceStatus LastStatus { get; set; } = PresenceStatus.Offline;
        public long UserVersion { get; set; }
    }
}
