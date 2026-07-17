using SimPle.Application.Lobbies.Services;

namespace SimPle.Infrastructure.Lobbies;

/// <summary>
/// Module 8 is not built. This reports that truthfully rather than pretending, and it is the single switch that
/// keeps every one of the brief's honesty promises enforceable:
/// Start returns <c>Lobbies.MatchRuntimeUnavailable</c>, the lobby stays <c>Open</c>, <c>allowedActions</c> omits
/// <c>start</c>, and <c>dependencyReadiness.matchRuntime</c> is <c>false</c> — so no client can invent a room
/// (Risk #6).
///
/// When M8 lands it replaces this registration. Nothing in the lobby commands changes.
/// </summary>
public sealed class NoMatchRuntimeProbe : IMatchRuntimeProbe
{
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>
    /// Always false — and this is a <em>true</em> answer, not a stub returning a placeholder. No match runtime
    /// exists, therefore no match exists, therefore no user is in one. The call site is real so that M8 has only
    /// to implement the probe, not to go find every place the question should have been asked.
    /// </summary>
    public Task<bool> IsInActiveMatchAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>
/// Module 7, backend session B (M07-B2): chat persistence and lobby-event fan-out are live (docs/specs/
/// module-07-realtime-presence-chat-spec.md) via <c>IChatService</c>/<c>ChatController</c>/hub
/// <c>SendLobbyMessage</c> — always true because chat runtime availability does not depend on any external process
/// being up (unlike the match runtime, it has no separate worker to poll).
/// </summary>
public sealed class LiveChatRuntimeProbe : IChatRuntimeProbe
{
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>
/// Module 9 owns AI participants. <c>aiFillRequested</c> is stored and displayed, but no AI seat can be created,
/// which is why a rated start is refused while it is set — a "ranked" match with an unfillable seat would either
/// hang or silently become unranked.
/// </summary>
public sealed class NoAiParticipantProbe : IAiParticipantProbe
{
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
}
