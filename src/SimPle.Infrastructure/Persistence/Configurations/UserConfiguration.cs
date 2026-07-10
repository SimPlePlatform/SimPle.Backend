using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Username).HasMaxLength(30).IsRequired();
        builder.Property(u => u.NormalizedUsername).HasMaxLength(30).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(254).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(254).IsRequired();
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.Bio).HasMaxLength(500);
        builder.Property(u => u.AvatarUrl).HasMaxLength(500);
        builder.Property(u => u.AvatarObjectKey).HasMaxLength(300);
        builder.Property(u => u.BannerUrl).HasMaxLength(500);
        builder.Property(u => u.BannerObjectKey).HasMaxLength(300);
        builder.Property(u => u.Color).HasMaxLength(20).IsRequired();
        builder.Property(u => u.BannerFallbackColor).HasMaxLength(20).IsRequired();
        builder.Property(u => u.Initials).HasMaxLength(4).IsRequired();
        builder.Property(u => u.Region).HasMaxLength(50).IsRequired();

        builder.Property(u => u.StatusMessage).HasMaxLength(100);

        builder.Property(u => u.Role).HasConversion<string>();
        builder.Property(u => u.Status).HasConversion<string>();
        builder.Property(u => u.SubscriptionTier).HasConversion<string>();
        builder.Property(u => u.Visibility).HasConversion<string>().HasDefaultValue(ProfileVisibility.Public);
        builder.Property(u => u.ProfileType).HasConversion<string>().HasDefaultValue(ProfileType.Player);

        builder.Property(u => u.GoogleId).HasMaxLength(255);

        // Unique indexes on normalized values for fast lookups
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
        builder.HasIndex(u => u.NormalizedUsername).IsUnique();
        // Sparse unique index: multiple users may have null GoogleId, but non-null values must be unique.
        builder.HasIndex(u => u.GoogleId).IsUnique().HasFilter("\"GoogleId\" IS NOT NULL");

        // NOTE: People-search prefix (LIKE 'X%') performance relies on two additional text_pattern_ops
        // indexes EF's fluent API cannot express — a plain btree index does not accelerate pattern-match
        // prefix scans under a non-C collation. Created via raw SQL in migration
        // 20260709120000_AddPeopleSearchAndSendCap: ix_users_normalizedusername_pattern (NormalizedUsername)
        // and ix_users_displayname_upper_pattern (expression index on upper("DisplayName")).
    }
}
