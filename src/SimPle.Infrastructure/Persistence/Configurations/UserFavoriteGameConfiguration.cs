using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Games;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class UserFavoriteGameConfiguration : IEntityTypeConfiguration<UserFavoriteGame>
{
    public void Configure(EntityTypeBuilder<UserFavoriteGame> builder)
    {
        builder.ToTable("user_favorite_games");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.IsActive).IsRequired();
        builder.Property(f => f.CycleId).IsRequired();

        builder.HasIndex(f => new { f.UserId, f.GameId }).IsUnique();

        builder.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Game>().WithMany().HasForeignKey(f => f.GameId).OnDelete(DeleteBehavior.Restrict);
    }
}
