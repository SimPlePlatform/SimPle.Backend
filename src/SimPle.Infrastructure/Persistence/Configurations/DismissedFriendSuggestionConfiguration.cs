using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class DismissedFriendSuggestionConfiguration : IEntityTypeConfiguration<DismissedFriendSuggestion>
{
    public void Configure(EntityTypeBuilder<DismissedFriendSuggestion> builder)
    {
        builder.ToTable("dismissed_friend_suggestions", t =>
        {
            t.HasCheckConstraint("ck_no_self_dismissal", @"""UserId"" != ""SuggestedUserId""");
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.DismissedAt).IsRequired();
        builder.Property(d => d.ExpiresAt).IsRequired();

        builder.HasIndex(d => new { d.UserId, d.SuggestedUserId })
            .IsUnique()
            .HasDatabaseName("ix_dismissed_suggestions_user_suggested");

        // Bounded cleanup job scans expired rows.
        builder.HasIndex(d => d.ExpiresAt).HasDatabaseName("ix_dismissed_suggestions_expiresat");

        builder.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.SuggestedUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
