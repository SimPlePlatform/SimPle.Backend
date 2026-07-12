namespace SimPle.Application.Lobbies.Services;

/// <summary>
/// Throttles <em>failed</em> join-credential attempts specifically (OWASP API4:2023).
///
/// This cannot be an ASP.NET rate-limit policy, and the distinction matters. A rate limiter spends a permit on
/// every request, so a policy tight enough to stop code-guessing (a wrong 60-bit code is cheap to spam) would
/// equally punish members legitimately joining lobbies they were invited to. Only failures are interesting, so
/// only failures are counted — a caller with a correct code is never throttled by this at all.
///
/// The counter is keyed on the authenticated actor, so it survives a client changing IPs, and it is bumped
/// <em>after</em> the constant-time digest comparison, so it leaks nothing about which part of a guess was right.
/// </summary>
public interface ILobbyJoinThrottle
{
    /// <summary>
    /// The instant the actor may next attempt a credential join, or null when they are not throttled.
    /// Returned rather than a bare bool so the caller can emit an accurate <c>Retry-After</c>.
    /// </summary>
    Task<DateTime?> GetRetryAfterUtcAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>Records one failed credential attempt. Called only on failure.</summary>
    Task RecordFailureAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>Clears the actor's failure streak after a successful join.</summary>
    Task ClearAsync(Guid actorUserId, CancellationToken ct = default);
}
