using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimPle.Application.Auth.DTOs;
using SimPle.Application.Common.Interfaces;
using SimPle.Infrastructure.Persistence;

namespace SimPle.IntegrationTests.Auth;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "auth-tests-" + Guid.NewGuid();
    private readonly bool _captchaSucceeds;

    /// <summary>Captures all emails sent during a test run so tokens can be extracted.</summary>
    public CapturingEmailService EmailCapture { get; } = new();

    /// <summary>Fake Google token validator — pre-program which tokens succeed and what profile they return.</summary>
    public FakeGoogleTokenValidationService GoogleValidator { get; } = new();

    public TestWebApplicationFactory(bool captchaSucceeds = true)
    {
        _captchaSucceeds = captchaSucceeds;
    }

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
                // Module 6: the API fails closed at startup when the join-credential key is unset, exactly as it
                // does for Jwt:SecretKey. Tests supply their own rather than the module shipping a dev fallback.
                ["LobbyCredential:Key"] = "integration-tests-lobby-credential-key-123456",
                ["LobbyCredential:DefaultRegion"] = "eu-west",
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
                ["Storage:Provider"] = "S3Compatible",
                ["Storage:BucketName"] = "integration-tests-profile-assets",
                ["Storage:Region"] = "us-east-1",
                ["Storage:ServiceUrl"] = "http://storage.invalid",
                ["Storage:AccessKey"] = "integration-tests-storage-access-key",
                ["Storage:SecretKey"] = "integration-tests-storage-secret-key",
                ["Storage:ProfilePrefix"] = "profile-assets",
                ["ConnectionStrings:DefaultConnection"] = "unused-for-in-memory-tests"
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
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.AddSingleton<ICaptchaVerificationService>(
                new TestCaptchaVerificationService(_captchaSucceeds));
            services.AddSingleton<IEmailService>(EmailCapture);
            services.AddSingleton<IGoogleTokenValidationService>(GoogleValidator);
            services.AddSingleton<IFileStorageService>(new TestFileStorageService());
        });
    }

    /// <summary>
    /// Extracts the raw token from the last verification URL captured by the email service.
    /// Returns null when no verification email has been captured yet.
    /// </summary>
    public string? GetLastVerificationToken() => ExtractToken(EmailCapture.LastVerificationUrl);

    /// <summary>
    /// Extracts the raw token from the last password-reset URL captured by the email service.
    /// Returns null when no reset email has been captured yet.
    /// </summary>
    public string? GetLastResetToken() => ExtractToken(EmailCapture.LastResetUrl);

    private static string? ExtractToken(string? url)
    {
        if (url is null) return null;
        var q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var segment in url[(q + 1)..].Split('&'))
        {
            var eq = segment.IndexOf('=');
            if (eq > 0 && segment[..eq] == "token")
                return Uri.UnescapeDataString(segment[(eq + 1)..]);
        }
        return null;
    }

    // ── Inner helpers ─────────────────────────────────────────────────────────

    private sealed class TestCaptchaVerificationService(bool succeeds) : ICaptchaVerificationService
    {
        public Task<bool> VerifyAsync(string responseToken, string? remoteIpAddress, CancellationToken ct = default) =>
            Task.FromResult(succeeds && responseToken == "test-captcha-token");
    }

    private sealed class TestFileStorageService : IFileStorageService
    {
        private readonly HashSet<string> _existingObjects = [];

        public Task<string> CreatePresignedPutUrlAsync(
            string objectKey, string contentType, TimeSpan expiresIn, CancellationToken ct = default)
        {
            _existingObjects.Add(objectKey);
            return Task.FromResult($"https://s3-upload.test/{Uri.EscapeDataString(objectKey)}");
        }

        public Task<string> CreatePresignedReadUrlAsync(
            string objectKey, TimeSpan expiresIn, CancellationToken ct = default) =>
            Task.FromResult($"https://s3-read.test/{Uri.EscapeDataString(objectKey)}");

        public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default) =>
            Task.FromResult(_existingObjects.Contains(objectKey));

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
        {
            _existingObjects.Remove(objectKey);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake Google token validator for integration tests.
    /// Call <see cref="SetupValid"/> before the test to program the expected credential.
    /// </summary>
    public sealed class FakeGoogleTokenValidationService : IGoogleTokenValidationService
    {
        private readonly Dictionary<string, GoogleUserInfo> _tokens = new();

        public void SetupValid(string token, GoogleUserInfo info) => _tokens[token] = info;

        public Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken ct = default) =>
            Task.FromResult(_tokens.TryGetValue(idToken, out var info) ? info : null);
    }

    /// <summary>Records sent emails so integration tests can inspect tokens without hitting SMTP.</summary>
    public sealed class CapturingEmailService : IEmailService
    {
        public string? LastVerificationUrl { get; private set; }
        public string? LastResetUrl { get; private set; }
        public int WelcomeEmailCount { get; private set; }
        public int PasswordChangedEmailCount { get; private set; }

        public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationUrl,
            CancellationToken ct = default)
        {
            LastVerificationUrl = verificationUrl;
            return Task.CompletedTask;
        }

        public Task SendWelcomeEmailAsync(string toEmail, string toName, CancellationToken ct = default)
        {
            WelcomeEmailCount++;
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetUrl,
            CancellationToken ct = default)
        {
            LastResetUrl = resetUrl;
            return Task.CompletedTask;
        }

        public Task SendPasswordChangedEmailAsync(string toEmail, string toName, CancellationToken ct = default)
        {
            PasswordChangedEmailCount++;
            return Task.CompletedTask;
        }
    }
}
