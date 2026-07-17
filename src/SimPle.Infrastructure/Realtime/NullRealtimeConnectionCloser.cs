using SimPle.Application.Common.Interfaces;

namespace SimPle.Infrastructure.Realtime;

/// <summary>
/// No-op default DI registration for <see cref="IRealtimeConnectionCloser"/>, so Auth flows (logout, logout-all,
/// revoke-session, delete-account) never throw when the realtime hub is disabled — e.g. as a rollback. The API
/// layer overrides this registration with a real SignalR-backed implementation when the hub is mapped (it needs
/// the concrete hub type, which Infrastructure cannot reference — see docs/specs/module-07-realtime-presence-chat-spec.md).
/// </summary>
public sealed class NullRealtimeConnectionCloser : IRealtimeConnectionCloser
{
    public Task CloseUserConnectionsAsync(Guid userId, string reason, CancellationToken ct = default) =>
        Task.CompletedTask;
}
