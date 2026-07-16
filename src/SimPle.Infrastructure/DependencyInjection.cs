using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Services;
using SimPle.Infrastructure.Auth;
using SimPle.Infrastructure.Email;
using SimPle.Infrastructure.Health;
using SimPle.Infrastructure.Lobbies;
using SimPle.Infrastructure.Matchmaking;
using SimPle.Infrastructure.Outbox;
using SimPle.Infrastructure.Persistence;
using SimPle.Infrastructure.Persistence.Repositories;
using SimPle.Infrastructure.Storage;

namespace SimPle.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<RecaptchaOptions>(configuration.GetSection(RecaptchaOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<GoogleOptions>(configuration.GetSection(GoogleOptions.SectionName));

        services.AddScoped<IPasswordHashingService, Argon2PasswordHasher>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddMemoryCache();
        services.AddSingleton<IRevokedJtiStore, MemoryCacheRevokedJtiStore>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IEmailVerificationTokenRepository, EmailVerificationTokenRepository>();
        services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IGoogleTokenValidationService, GoogleTokenValidationService>();
        services.AddScoped<IProfileRepository, ProfileRepository>();
        services.AddScoped<IUsernameChangeRequestRepository, UsernameChangeRequestRepository>();
        services.AddScoped<IRetiredUsernameRepository, RetiredUsernameRepository>();
        services.AddScoped<IFriendRepository, FriendRepository>();
        services.AddScoped<IGameRepository, GameRepository>();

        // Module 6 — injected clock (R4). Scoped to M6-owned code only: the 66 pre-existing raw DateTime.UtcNow
        // call sites across the codebase are deliberately NOT refactored, because a half-done cross-cutting clock
        // change would be worse than none. M6's own code takes TimeProvider so the mandatory fake-clock tests of
        // the 15/30/60-second bands and the 2h/30min expiries (brief Risk #8) are actually provable.
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<ILobbyCredentialHasher, HmacLobbyCredentialHasher>();
        services.AddScoped<ILobbyRepository, LobbyRepository>();

        // R3 — reruns a whole lobby command (read + decide + write) on contention and surfaces a typed conflict.
        // Scoped, because it clears the change tracker of the same scoped AppDbContext the command reads through.
        services.AddScoped<ILobbyCommandRunner, LobbyCommandRunner>();

        // Honest dependency probes. Every one reports "not available" because M7/M8/M9 do not exist — which is
        // what makes Start a 503, `allowedActions` omit `start`, and the UI's disabled controls truthful rather
        // than decorative. Each is replaced, not rewritten, when its module lands.
        services.AddSingleton<IMatchRuntimeProbe, NoMatchRuntimeProbe>();
        services.AddSingleton<IChatRuntimeProbe, NoChatRuntimeProbe>();
        services.AddSingleton<IAiParticipantProbe, NoAiParticipantProbe>();

        services.AddSingleton<ILobbyJoinThrottle, MemoryCacheLobbyJoinThrottle>();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.PostConfigure<StorageOptions>(options =>
        {
            ApplyStorageEnvironmentFallbacks(options, configuration);
        });
        services.AddScoped<IFileStorageService, S3FileStorageService>();
        services.AddHttpClient<ICaptchaVerificationService, GoogleRecaptchaV2Service>();

        // Readiness is intentionally process-local: this application is deployed as a single backend instance, so
        // these durable workers and the API must share one lifecycle until a later distributed design is approved.
        services.AddSingleton<IWorkerReadinessRegistry>(_ => new WorkerReadinessRegistry(RequiredWorkers.All));

        services.Configure<TokenCleanupOptions>(
            configuration.GetSection(TokenCleanupOptions.SectionName));
        services.AddHostedService<TokenCleanupService>();

        services.Configure<DismissedSuggestionCleanupOptions>(
            configuration.GetSection(DismissedSuggestionCleanupOptions.SectionName));
        services.AddHostedService<DismissedSuggestionCleanupService>();

        // ── Module 6, slice 6C — matchmaking, expiry, and the outbox dispatcher ──

        services.AddScoped<IMatchmakingRepository, MatchmakingRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();

        // ILobbyCommandRunner's sibling for background work: same transaction and contention semantics, no advisory
        // lock (a worker has no actor to serialize).
        services.AddScoped<IWorkerTransaction, WorkerTransaction>();

        services.Configure<MatchmakingOptions>(configuration.GetSection(MatchmakingOptions.SectionName));
        services.Configure<ExpiryOptions>(configuration.GetSection(ExpiryOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        // All three honour a WorkerEnabled flag and simply do not start when it is false — that is how the rollback
        // plan disables the workers while preserving every lobby and ticket record.
        services.AddHostedService<MatchmakingWorker>();
        services.AddHostedService<LobbyExpiryWorker>();
        services.AddHostedService<OutboxDispatcherWorker>();

        return services;
    }

    private static void ApplyStorageEnvironmentFallbacks(StorageOptions options, IConfiguration configuration)
    {
        options.Provider = configuration["Storage__Provider"] ?? options.Provider;
        options.BucketName = configuration["Storage__BucketName"] ?? options.BucketName;
        options.Region = configuration["Storage__Region"] ?? options.Region;
        options.ServiceUrl = configuration["Storage__ServiceUrl"] ?? options.ServiceUrl;
        options.AccessKey = configuration["Storage__AccessKey"] ?? options.AccessKey;
        options.SecretKey = configuration["Storage__SecretKey"] ?? options.SecretKey;
        options.ProfilePrefix = configuration["Storage__ProfilePrefix"] ?? options.ProfilePrefix;
        if (bool.TryParse(configuration["Storage__ForcePathStyle"], out var forcePathStyle))
            options.ForcePathStyle = forcePathStyle;
        if (int.TryParse(configuration["Storage__UploadUrlExpiryMinutes"], out var upload))
            options.UploadUrlExpiryMinutes = upload;
        if (int.TryParse(configuration["Storage__ReadUrlExpiryMinutes"], out var read))
            options.ReadUrlExpiryMinutes = read;
    }
}
