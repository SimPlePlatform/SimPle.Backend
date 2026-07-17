using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Infrastructure.Health;

namespace SimPle.Infrastructure.Auth;

// Background service that periodically deletes expired auth tokens.
// Runs on a configurable interval (default: every 6 hours).
// Deletes tokens that expired more than RetentionPeriod ago (default: 1 day).
public sealed class TokenCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TokenCleanupService> _logger;
    private readonly TokenCleanupOptions _options;
    private readonly IWorkerReadinessRegistry _readiness;

    public TokenCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<TokenCleanupService> logger,
        IOptions<TokenCleanupOptions> options,
        IWorkerReadinessRegistry readiness)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _readiness = readiness;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _readiness.MarkStarted(RequiredWorkers.TokenCleanup);

        // Stagger the first run so it doesn't run immediately on startup.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCleanupAsync(stoppingToken);
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - _options.RetentionPeriod;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var refreshRepo = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
            var emailVerificationRepo = scope.ServiceProvider.GetRequiredService<IEmailVerificationTokenRepository>();
            var passwordResetRepo = scope.ServiceProvider.GetRequiredService<IPasswordResetTokenRepository>();

            var refreshDeleted = await refreshRepo.DeleteExpiredAsync(cutoff, ct);
            var emailVerifDeleted = await emailVerificationRepo.DeleteExpiredAsync(cutoff, ct);
            var passwordResetDeleted = await passwordResetRepo.DeleteExpiredAsync(cutoff, ct);

            var total = refreshDeleted + emailVerifDeleted + passwordResetDeleted;
            if (total > 0)
                _logger.LogInformation(
                    "Token cleanup: deleted {Refresh} refresh, {EmailVerif} email-verification, {PasswordReset} password-reset rows (cutoff: {Cutoff:u})",
                    refreshDeleted, emailVerifDeleted, passwordResetDeleted, cutoff);

            _readiness.MarkHealthy(RequiredWorkers.TokenCleanup);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _readiness.MarkUnhealthy(RequiredWorkers.TokenCleanup);
            _logger.LogError(ex, "Token cleanup failed. Will retry in {Interval}.", _options.Interval);
        }
    }
}
