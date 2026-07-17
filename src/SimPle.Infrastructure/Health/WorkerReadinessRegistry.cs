using System.Collections.Concurrent;

namespace SimPle.Infrastructure.Health;

/// <summary>
/// The worker names required for this single-process deployment. Keeping them central makes the readiness
/// contract explicit: adding a durable background worker means adding its heartbeat here too.
/// </summary>
public static class RequiredWorkers
{
    public const string TokenCleanup = "token-cleanup";
    public const string DismissedSuggestionCleanup = "dismissed-suggestion-cleanup";
    public const string Matchmaking = "matchmaking";
    public const string LobbyExpiry = "lobby-expiry";
    public const string OutboxDispatcher = "outbox-dispatcher";

    /// <summary>Module 7, backend session B (M07-B2): the chat retention sweep
    /// (<see cref="SimPle.Infrastructure.Chat.ChatRetentionSweeper"/>).</summary>
    public const string ChatRetention = "chat-retention";

    public static readonly IReadOnlyCollection<string> All =
    [
        TokenCleanup,
        DismissedSuggestionCleanup,
        Matchmaking,
        LobbyExpiry,
        OutboxDispatcher,
        ChatRetention,
    ];
}

/// <summary>
/// Process-local worker readiness state. It deliberately stores no exception, configuration, or dependency detail,
/// because readiness is exposed to an unauthenticated infrastructure probe.
/// </summary>
public interface IWorkerReadinessRegistry
{
    bool AreRequiredWorkersReady { get; }

    void MarkStarted(string workerName);
    void MarkHealthy(string workerName);
    void MarkUnhealthy(string workerName);
}

public sealed class WorkerReadinessRegistry : IWorkerReadinessRegistry
{
    private readonly ConcurrentDictionary<string, WorkerState> _states;

    public WorkerReadinessRegistry(IEnumerable<string> requiredWorkers)
    {
        _states = new ConcurrentDictionary<string, WorkerState>(
            requiredWorkers.Select(worker => new KeyValuePair<string, WorkerState>(worker, WorkerState.NotStarted)),
            StringComparer.Ordinal);
    }

    public bool AreRequiredWorkersReady => _states.Count > 0 && _states.Values.All(state => state == WorkerState.Healthy);

    public void MarkStarted(string workerName) => SetState(workerName, WorkerState.Healthy);

    public void MarkHealthy(string workerName) => SetState(workerName, WorkerState.Healthy);

    public void MarkUnhealthy(string workerName) => SetState(workerName, WorkerState.Unhealthy);

    private void SetState(string workerName, WorkerState state)
    {
        if (!_states.ContainsKey(workerName))
            throw new ArgumentOutOfRangeException(nameof(workerName), "The worker is not part of the readiness contract.");

        _states[workerName] = state;
    }

    private enum WorkerState
    {
        NotStarted,
        Healthy,
        Unhealthy,
    }
}
