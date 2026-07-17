using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimPle.Infrastructure.Persistence;

namespace SimPle.Api.Health;

/// <summary>
/// Verifies that PostgreSQL accepts a connection and that the deployed schema is current. The endpoint response is
/// intentionally generic; diagnostic detail remains in normal application logs and deployment tooling.
/// </summary>
public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseReadinessHealthCheck> _logger;

    public DatabaseReadinessHealthCheck(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseReadinessHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Database readiness check failed.");

            // WebApplicationFactory uses EF's in-memory provider. Production uses Npgsql, where this additionally
            // prevents the app from receiving traffic before its migration job has completed.
            if (db.Database.IsRelational() && (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Database readiness check failed.");

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Database readiness check failed.");
            return HealthCheckResult.Unhealthy("Database readiness check failed.");
        }
    }
}
