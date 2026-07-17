namespace SimPle.Application.Realtime.Presence;

/// <summary>
/// A single presence change worth telling clients about. <see cref="Changed"/> is false when the call was a no-op
/// (status didn't actually change) — callers must not fan out a <c>PresenceChanged</c> event when this is false,
/// or <c>UserVersion</c> would appear to move without a real change.
/// </summary>
public sealed record PresenceUpdateResult(bool Changed, PresenceStatus Status, Guid ServerEpoch, long UserVersion);

/// <summary>
/// Tracks per-user, per-connection presence in memory only (no persistence, no cross-instance sync — single
/// instance for B1, see spec Risk Register). Every mutating/query method is lazy: status is computed from
/// timestamps against the injected <see cref="TimeProvider"/> at call time, there is no background timer.
/// </summary>
public interface IPresenceRegistry
{
    /// <summary>Stable per-instance identifier, regenerated on every process restart. Clients compare this
    /// against their cached value and discard stale presence when it changes.</summary>
    Guid ServerEpoch { get; }

    /// <summary>Registers a new connection for the user. Returns false (and registers nothing) if the user is
    /// already at the connection cap — the hub must reject the connection with <c>Realtime.ConnectionLimit</c>.</summary>
    bool TryConnect(Guid userId, string connectionId);

    /// <summary>Removes one connection. If it was the user's last live connection, the user enters the offline
    /// debounce window rather than going Offline immediately.</summary>
    void Disconnect(Guid userId, string connectionId);

    /// <summary>Records activity on one connection, resetting its away timer. Throttled to at most once per 60s
    /// per connection — a rejected (throttled) signal returns false and mutates nothing.</summary>
    bool TryReportActivity(Guid userId, string connectionId);

    /// <summary>Adds the lobby to the user's membership set (drives <see cref="PresenceStatus.InLobby"/>). Distinct
    /// from subscription — subscribing to a lobby's updates never by itself implies membership presence.</summary>
    PresenceUpdateResult SetLobbyMembership(Guid userId, Guid lobbyId, bool isMember);

    /// <summary>Computes the user's current aggregated status as of now, evaluating away/offline debounce lazily.
    /// Returns Offline with no user-version history if the user has never connected.</summary>
    PresenceUpdateResult GetStatus(Guid userId);
}
