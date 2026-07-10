using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Infrastructure.Auth;
using SimPle.Infrastructure.Email;
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
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.PostConfigure<StorageOptions>(options =>
        {
            ApplyStorageEnvironmentFallbacks(options, configuration);
        });
        services.AddScoped<IFileStorageService, S3FileStorageService>();
        services.AddHttpClient<ICaptchaVerificationService, GoogleRecaptchaV2Service>();

        services.Configure<TokenCleanupOptions>(
            configuration.GetSection(TokenCleanupOptions.SectionName));
        services.AddHostedService<TokenCleanupService>();

        services.Configure<DismissedSuggestionCleanupOptions>(
            configuration.GetSection(DismissedSuggestionCleanupOptions.SectionName));
        services.AddHostedService<DismissedSuggestionCleanupService>();

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
