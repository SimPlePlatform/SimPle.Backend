using SimPle.Application.Common.Interfaces;
using SimPle.Shared.Common;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// A pass-through <see cref="ILobbyCommandRunner"/>.
///
/// The retry and the advisory lock it performs only mean anything against a database that actually enforces unique
/// indexes and row versions, so they are proven in the real-PostgreSQL suite. Substituting a fake that "simulated"
/// contention here would prove only that the fake works.
/// </summary>
public sealed class PassThroughCommandRunner : ILobbyCommandRunner
{
    public Task<Result<T>> RunAsync<T>(
        Guid actorUserId, Func<CancellationToken, Task<Result<T>>> command, CancellationToken ct = default) =>
        command(ct);
}

/// <summary>Pass-through <see cref="IWorkerTransaction"/>, for the same reason.</summary>
public sealed class PassThroughWorkerTransaction : IWorkerTransaction
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) => work(ct);
}
