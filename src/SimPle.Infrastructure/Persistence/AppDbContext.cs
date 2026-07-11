using Microsoft.EntityFrameworkCore;
using SimPle.Domain.Friends;
using SimPle.Domain.Games;
using SimPle.Domain.Outbox;
using SimPle.Domain.Profiles;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    // Module 2 — profile social identity
    public DbSet<ProfileExternalLink> ProfileExternalLinks => Set<ProfileExternalLink>();
    public DbSet<ProfileInterestTag> ProfileInterestTags => Set<ProfileInterestTag>();
    public DbSet<UsernameChangeRequest> UsernameChangeRequests => Set<UsernameChangeRequest>();
    public DbSet<RetiredUsername> RetiredUsernames => Set<RetiredUsername>();

    // Module 3 — friends & social graph
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<UserFriendSettings> UserFriendSettings => Set<UserFriendSettings>();
    public DbSet<DismissedFriendSuggestion> DismissedFriendSuggestions => Set<DismissedFriendSuggestion>();

    // Module 3 — transactional integration-event outbox (consumed by M7/M10/M11)
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();

    // Module 4 — game library & discovery
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameTag> GameTags => Set<GameTag>();
    public DbSet<GameModeCapability> GameModeCapabilities => Set<GameModeCapability>();
    public DbSet<UserFavoriteGame> UserFavoriteGames => Set<UserFavoriteGame>();
    public DbSet<CatalogSeedHistory> CatalogSeedHistory => Set<CatalogSeedHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
