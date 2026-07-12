using Microsoft.Extensions.Caching.Memory;
using SimPle.Application.Lobbies.Services;

namespace SimPle.Infrastructure.Lobbies;

/// <summary>
/// Counts <em>failed</em> join-credential attempts per account and locks the actor out of the join endpoint once
/// they cross the threshold (OWASP API4:2023).
///
/// <para>
/// This is not an ASP.NET rate-limit policy, and the difference is the point. A rate limiter spends a permit on
/// every request, so a window tight enough to make guessing a 60-bit code hopeless would equally punish a member
/// legitimately joining lobbies they were invited to. Only failures are counted here, so a caller who supplies
/// correct credentials is never throttled by this at all, no matter how often they join.
/// </para>
///
/// <para>
/// Backed by <see cref="IMemoryCache"/>, the same in-process store the revoked-JTI cache already uses. That means
/// it is <strong>per-instance</strong>: an attacker spraying codes across N app instances gets N times the budget.
/// The pre-existing per-account rate-limit policies have exactly this property, so this adds no new class of gap —
/// but it is recorded as a known limitation rather than papered over, and a distributed store is the fix when the
/// platform runs more than one instance.
/// </para>
/// </summary>
public sealed class MemoryCacheLobbyJoinThrottle : ILobbyJoinThrottle
{
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Ten wrong codes buys a five-minute lockout. Against a 60-bit space, ten guesses per five minutes is not a
    /// meaningful search — the entropy does the real work, and this only removes the free unlimited retries that
    /// would let a bot grind the space over weeks.
    /// </summary>
    private const int MaxFailures = 10;

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    public MemoryCacheLobbyJoinThrottle(IMemoryCache cache, TimeProvider clock)
    {
        _cache = cache;
        _clock = clock;
    }

    public Task<DateTime?> GetRetryAfterUtcAsync(Guid actorUserId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<FailureStreak>(Key(actorUserId), out var streak)
            && streak is not null
            && streak.Count >= MaxFailures)
        {
            return Task.FromResult<DateTime?>(streak.WindowEndsAtUtc);
        }

        return Task.FromResult<DateTime?>(null);
    }

    public Task RecordFailureAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var key = Key(actorUserId);
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        var streak = _cache.TryGetValue<FailureStreak>(key, out var existing) && existing is not null
            ? existing with { Count = existing.Count + 1 }
            // The window is anchored at the *first* failure and does not slide. A sliding window would let a
            // patient attacker sit just under the threshold forever, refreshing it with every guess.
            : new FailureStreak(1, nowUtc + FailureWindow);

        _cache.Set(key, streak, streak.WindowEndsAtUtc);
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid actorUserId, CancellationToken ct = default)
    {
        _cache.Remove(Key(actorUserId));
        return Task.CompletedTask;
    }

    private static string Key(Guid actorUserId) => $"lobby-join-failures:{actorUserId:N}";

    private sealed record FailureStreak(int Count, DateTime WindowEndsAtUtc);
}
