namespace SimPle.Application.Realtime;

/// <summary>
/// Hub-invocation rate limiting. ASP.NET Core's built-in <c>Microsoft.AspNetCore.RateLimiting</c> middleware only
/// applies to HTTP endpoints, not SignalR hub method invocations, so this is a small hand-rolled abstraction over
/// <c>System.Threading.RateLimiting.PartitionedRateLimiter&lt;Guid&gt;</c> (docs/specs/module-07-realtime-presence-chat-spec.md).
/// Unused by any caller in B1 (no <c>SendLobbyMessage</c> exists yet) but declared now so B2's chat send path has
/// a stable rate-limiting contract to call into.
/// </summary>
public interface IRealtimeRateLimiter
{
    /// <summary>True if <paramref name="userId"/> may open one more realtime connection (max 5 concurrent).
    /// Does not reserve/consume anything by itself — pair with <see cref="ReleaseConnection"/> on disconnect.</summary>
    bool TryAcquireConnection(Guid userId);

    /// <summary>Releases one connection slot previously acquired via <see cref="TryAcquireConnection"/>.</summary>
    void ReleaseConnection(Guid userId);

    /// <summary>True if <paramref name="userId"/> may send one more message right now: 5 per 5s burst AND
    /// 20 per 60s sustained — both must pass.</summary>
    bool TryAcquireMessage(Guid userId);
}
