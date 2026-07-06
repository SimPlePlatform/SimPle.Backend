using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class UserFriendSettingsConfiguration : IEntityTypeConfiguration<UserFriendSettings>
{
    public void Configure(EntityTypeBuilder<UserFriendSettings> builder)
    {
        builder.ToTable("user_friend_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FriendRequestPrivacy)
            .HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.HasIndex(s => s.UserId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
