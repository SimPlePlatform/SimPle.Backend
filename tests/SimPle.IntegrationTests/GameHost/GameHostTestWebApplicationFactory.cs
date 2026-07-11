using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimPle.Application.Auth.DTOs;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.GameHost.Services;
using SimPle.Domain.GameHost;
using SimPle.Infrastructure.Persistence;

namespace SimPle.IntegrationTests.GameHost;

/// <summary>
/// Boots the real <c>SimPle.Api</c> composition root — the same <c>Program.cs</c> every other integration suite
/// exercises — with two Module-5-specific overrides: an <see cref="AppDbContext"/> pinned to an isolated
/// InMemory database this test controls the name of (so a test can seed catalog rows before the host's startup
/// scope reads them), and an optional replacement <see cref="IGameRegistry"/> standing in for the "zero engines
/// installed" production default. Everything else (JWT/Recaptcha option validation, other DbContext-dependent
/// services, etc.) is configured exactly as <see cref="Auth.TestWebApplicationFactory"/> configures it, since the
/// same startup path runs regardless of which module's tests are booting it.
/// </summary>
public sealed class GameHostTestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The EF Core InMemory provider shares one process-wide store per database name when no explicit
    /// <c>InMemoryDatabaseRoot</c> is supplied, regardless of which service provider created the
    /// <see cref="AppDbContext"/>. Seeding a database with this name via a context built outside the factory's DI
    /// container — before touching <see cref="WebApplicationFactory{TEntryPoint}.Services"/> — is therefore
    /// visible to Program.cs's own startup catalog-compatibility scope, which runs before any test code can reach
    /// into the DI-resolved context to seed it the usual (post-boot) way.
    /// </summary>
    public string DatabaseName { get; } = "gamehost-tests-" + Guid.NewGuid();

    private readonly IReadOnlyList<IHostedGameDefinition> _engines;

    public GameHostTestWebApplicationFactory(params IHostedGameDefinition[] engines)
    {
        _engines = engines;
    }

    /// <summary>Opens a context bound to <see cref="DatabaseName"/>, independent of this factory's DI container.</summary>
    public AppDbContext OpenDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DatabaseName).Options);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "integration-tests-use-only-this-secret-value-123456",
                ["Jwt:Issuer"] = "SimPle.Tests",
                ["Jwt:Audience"] = "SimPle.Tests",
                ["Jwt:ExpiryMinutes"] = "15",
                ["Auth:RefreshTokenExpiryDays"] = "7",
                ["Auth:MaxFailedLoginAttempts"] = "10",
                ["Auth:LockoutDurationMinutes"] = "15",
                ["Recaptcha:SecretKey"] = "integration-tests-recaptcha-secret",
                ["Recaptcha:VerificationUrl"] = "https://captcha.invalid/siteverify",
                ["Email:From"] = "test@example.com",
                ["Email:FromName"] = "SimPle Tests",
                ["Email:SmtpHost"] = "smtp.example.com",
                ["Email:SmtpPort"] = "587",
                ["Email:Username"] = "test@example.com",
                ["Email:Password"] = "test-password",
                ["Email:AppUrl"] = "http://localhost:3000",
                ["Google:ClientId"] = "integration-tests-google-client-id",
                ["ConnectionStrings:DefaultConnection"] = "unused-for-in-memory-tests",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<ICaptchaVerificationService>();
            services.RemoveAll<IEmailService>();
            services.RemoveAll<IGoogleTokenValidationService>();
            services.RemoveAll<IFileStorageService>();
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(DatabaseName));
            services.AddSingleton<ICaptchaVerificationService>(new AlwaysSucceedsCaptchaService());
            services.AddSingleton<IEmailService>(new DiscardingEmailService());
            services.AddSingleton<IGoogleTokenValidationService>(new NeverValidatesGoogleService());
            services.AddSingleton<IFileStorageService>(new NullFileStorageService());

            // Production installs zero engines (see Program.cs); tests override with fakes/references to exercise
            // the "at least one engine installed" paths that the default composition root cannot reach today.
            services.RemoveAll<IGameRegistry>();
            services.AddSingleton<IGameRegistry>(_ => GameRegistry.Create(_engines));
        });
    }

    private sealed class AlwaysSucceedsCaptchaService : ICaptchaVerificationService
    {
        public Task<bool> VerifyAsync(string responseToken, string? remoteIpAddress, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class DiscardingEmailService : IEmailService
    {
        public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendWelcomeEmailAsync(string toEmail, string toName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendPasswordChangedEmailAsync(string toEmail, string toName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NeverValidatesGoogleService : IGoogleTokenValidationService
    {
        public Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken ct = default) =>
            Task.FromResult<GoogleUserInfo?>(null);
    }

    private sealed class NullFileStorageService : IFileStorageService
    {
        public Task<string> CreatePresignedPutUrlAsync(string objectKey, string contentType, TimeSpan expiresIn, CancellationToken ct = default) =>
            Task.FromResult($"https://s3-upload.test/{Uri.EscapeDataString(objectKey)}");

        public Task<string> CreatePresignedReadUrlAsync(string objectKey, TimeSpan expiresIn, CancellationToken ct = default) =>
            Task.FromResult($"https://s3-read.test/{Uri.EscapeDataString(objectKey)}");

        public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default) => Task.FromResult(false);

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
    }
}
