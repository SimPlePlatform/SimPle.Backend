namespace SimPle.Application.Common.Interfaces;

/// <summary>
/// Proactively closes a user's mapped realtime (SignalR) connections. This is one of three independent
/// mechanisms Module 7's authorization model requires (see docs/specs/module-07-realtime-presence-chat-spec.md,
/// "The load-bearing rule"): SignalR caches the authenticated principal for the connection's lifetime and does
/// not revalidate it automatically, so logout / logout-all / revoke-session / delete-account must proactively
/// close any live sockets rather than relying on token-expiry alone.
///
/// The default DI registration is <c>NullRealtimeConnectionCloser</c> — a no-op — so that Auth flows never throw
/// when the realtime hub is disabled (e.g. as a rollback). The real SignalR-backed implementation lives in the
/// API layer (it needs the concrete hub type) and overrides this registration when the hub is wired up.
/// </summary>
public interface IRealtimeConnectionCloser
{
    /// <summary>
    /// Closes every realtime connection currently mapped to <paramref name="userId"/>, if any. Never throws for a
    /// user with no connections.
    /// </summary>
    /// <param name="reason">
    /// One of: <c>lobby.membership_removed</c>, <c>lobby.closed</c>, <c>auth.session_revoked</c>,
    /// <c>auth.suspended</c>, <c>social.blocked</c> — sent to the client as <c>AccessRevoked</c> before closing.
    /// </param>
    Task CloseUserConnectionsAsync(Guid userId, string reason, CancellationToken ct = default);
}
