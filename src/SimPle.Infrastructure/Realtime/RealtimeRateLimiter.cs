using System.Threading.RateLimiting;
using SimPle.Application.Realtime;

namespace SimPle.Infrastructure.Realtime;

/// <summary>
/// Hand-rolled hub-invocation rate limiter (docs/specs/module-07-realtime-presence-chat-spec.md): 5 connections
/// per user, 5 messages/5s burst chained with 20 messages/60s sustained (both must pass). Not SignalR-specific —
/// pure <c>System.Threading.RateLimiting</c> — so it lives in Infrastructure rather than the API layer.
/// </summary>
public sealed class RealtimeRateLimiter : IRealtimeRateLimiter, IDisposable
{
    private const int MaxConnectionsPerUser = 5;
    private const int BurstPermits = 5;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);
    private const int SustainedPermits = 20;
    private static readonly TimeSpan SustainedWindow = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<Guid> _connectionLimiter;
    private readonly PartitionedRateLimiter<Guid> _burstLimiter;
    private readonly PartitionedRateLimiter<Guid> _sustainedLimiter;

    // One lease per acquired connection, per user — a ConcurrencyLimiter permit is only released by disposing the
    // exact lease that acquired it, so a single "latest lease" field per user would leak permits from earlier
    // concurrent connections. Push on acquire, pop on release (release order need not match acquire order; any
    // held lease releases one permit).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentStack<RateLimitLease>> _connectionLeases = new();

    public RealtimeRateLimiter()
    {
        _connectionLimiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetConcurrencyLimiter(userId, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = MaxConnectionsPerUser,
                QueueLimit = 0,
            }));

        _burstLimiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = BurstPermits,
                Window = BurstWindow,
                QueueLimit = 0,
            }));

        _sustainedLimiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = SustainedPermits,
                Window = SustainedWindow,
                QueueLimit = 0,
            }));
    }

    public bool TryAcquireConnection(Guid userId)
    {
        var lease = _connectionLimiter.AttemptAcquire(userId);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return false;
        }

        // Belt-and-suspenders redundant cap alongside the presence registry's own 5-connection enforcement
        // (deliberate; see final report).
        var stack = _connectionLeases.GetOrAdd(userId, _ => new System.Collections.Concurrent.ConcurrentStack<RateLimitLease>());
        stack.Push(lease);
        return true;
    }

    public void ReleaseConnection(Guid userId)
    {
        if (_connectionLeases.TryGetValue(userId, out var stack) && stack.TryPop(out var lease))
            lease.Dispose();
    }

    public bool TryAcquireMessage(Guid userId)
    {
        using var burstLease = _burstLimiter.AttemptAcquire(userId);
        if (!burstLease.IsAcquired)
            return false;

        using var sustainedLease = _sustainedLimiter.AttemptAcquire(userId);
        return sustainedLease.IsAcquired;
    }

    public void Dispose()
    {
        _connectionLimiter.Dispose();
        _burstLimiter.Dispose();
        _sustainedLimiter.Dispose();
        foreach (var stack in _connectionLeases.Values)
            while (stack.TryPop(out var lease))
                lease.Dispose();
    }
}
