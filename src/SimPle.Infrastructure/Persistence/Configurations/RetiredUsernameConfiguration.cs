using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Profiles;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class RetiredUsernameConfiguration : IEntityTypeConfiguration<RetiredUsername>
{
    public void Configure(EntityTypeBuilder<RetiredUsername> builder)
    {
        builder.ToTable("retired_usernames");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.NormalizedUsername).HasMaxLength(30).IsRequired();
        builder.Property(r => r.PriorOwnerUserId).IsRequired();
        builder.Property(r => r.RetiredAtUtc).IsRequired();

        builder.HasIndex(r => r.NormalizedUsername).IsUnique();
        builder.HasIndex(r => r.PriorOwnerUserId);

        // Deliberately no HasOne<User>() FK: a retired username must remain non-reassignable even after
        // the prior owner's account is later deleted (see RetiredUsername doc comment).
    }
}
