using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Options;

namespace SimPle.Api.Health;

/// <summary>
/// Confirms that the S3-compatible storage integration is configured without issuing a network call on every probe.
/// Media reachability belongs to deployment smoke tests; a readiness endpoint must remain fast and side-effect free.
/// </summary>
public sealed class StorageConfigurationHealthCheck : IHealthCheck
{
    private readonly IOptions<StorageOptions> _options;

    public StorageConfigurationHealthCheck(IOptions<StorageOptions> options) => _options = options;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var isConfigured =
            IsConfiguredValue(options.Provider) &&
            IsConfiguredValue(options.BucketName) &&
            IsConfiguredValue(options.Region) &&
            IsConfiguredValue(options.AccessKey) &&
            IsConfiguredValue(options.SecretKey) &&
            IsConfiguredValue(options.ProfilePrefix) &&
            options.UploadUrlExpiryMinutes > 0 &&
            options.ReadUrlExpiryMinutes > 0 &&
            (string.IsNullOrWhiteSpace(options.ServiceUrl) ||
             Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri) &&
             (serviceUri.Scheme == Uri.UriSchemeHttp || serviceUri.Scheme == Uri.UriSchemeHttps));

        return Task.FromResult(isConfigured
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Storage readiness check failed."));
    }

    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("CONFIGURE", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
}
