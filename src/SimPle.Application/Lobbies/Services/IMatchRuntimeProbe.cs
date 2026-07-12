namespace SimPle.Application.Lobbies.Services;

/// <summary>
/// Module 8's readiness probe, as seen from Module 6.
///
/// M8 does not exist. Rather than scatter <c>// TODO: M8</c> through the command layer, M6 builds the real call
/// site now against this interface and ships an implementation that honestly reports "no runtime". When M8 lands
/// it replaces the implementation and every gate below turns on without a single change to the lobby commands.
///
/// This is what keeps the brief's central promise enforceable: until a consumer is registered, Start returns
/// <c>Lobbies.MatchRuntimeUnavailable</c>, the lobby stays <c>Open</c>, and nothing anywhere claims a match was
/// created (Risk #6).
/// </summary>
public interface IMatchRuntimeProbe
{
    /// <summary>
    /// Whether a match runtime is registered and healthy. False in Module 6, which is what makes Start honestly
    /// unavailable rather than silently broken.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the user is a participant in a live (<c>PendingStart|Active|PausePending|Paused</c>) match.
    ///
    /// Re-asked at join, invite-accept, enqueue, assignment, and start rather than once up front — the state can
    /// change between steps, so a single check would be a race, not a guarantee (brief Risk #2).
    ///
    /// With no runtime there are no matches, so this is trivially false today. That is a true answer, not a stub:
    /// a user genuinely cannot be in a live match when no match can exist.
    /// </summary>
    Task<bool> IsInActiveMatchAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Module 7's live-delivery readiness, and Module 9's AI participants. Both are surfaced to the client through
/// <c>dependencyReadiness</c> so the UI renders an honest disabled control naming the owning module instead of
/// hardcoding "coming soon" copy it would later have to hunt down and remove.
/// </summary>
public interface IChatRuntimeProbe
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public interface IAiParticipantProbe
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
