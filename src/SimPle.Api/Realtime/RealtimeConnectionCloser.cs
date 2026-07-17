using SimPle.Application.Common.Interfaces;
using SimPle.Application.Realtime.Contracts;

namespace SimPle.Api.Realtime;

/// <summary>
/// Real, SignalR-backed <see cref="IRealtimeConnectionCloser"/>: one of the three independent mechanisms Module
/// 7's authorization model requires (docs/specs/module-07-realtime-presence-chat-spec.md, "the load-bearing
/// rule"). Sends <c>AccessRevoked</c> (best-effort) then forcibly aborts every live connection for the user via
/// <see cref="RealtimeConnectionTracker"/>, rather than waiting for token expiry or the next per-method recheck.
/// </summary>
public sealed class RealtimeConnectionCloser : IRealtimeConnectionCloser
{
    private readonly IRealtimeNotifier _notifier;
    private readonly RealtimeConnectionTracker _tracker;

    public RealtimeConnectionCloser(IRealtimeNotifier notifier, RealtimeConnectionTracker tracker)
    {
        _notifier = notifier;
        _tracker = tracker;
    }

    public async Task CloseUserConnectionsAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        await _notifier.NotifyAccessRevokedAsync(userId, reason, ct);
        _tracker.AbortAll(userId);
    }
}
