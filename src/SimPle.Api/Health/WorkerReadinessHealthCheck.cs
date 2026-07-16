using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimPle.Infrastructure.Health;

namespace SimPle.Api.Health;

/// <summary>Ensures every required in-process background worker has started and has not reported a failed cycle.</summary>
public sealed class WorkerReadinessHealthCheck : IHealthCheck
{
    private readonly IWorkerReadinessRegistry _workers;

    public WorkerReadinessHealthCheck(IWorkerReadinessRegistry workers) => _workers = workers;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_workers.AreRequiredWorkersReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Background worker readiness check failed."));
}
