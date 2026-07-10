using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Games;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class GameTagConfiguration : IEntityTypeConfiguration<GameTag>
{
    public void Configure(EntityTypeBuilder<GameTag> builder)
    {
        builder.ToTable("game_tags");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Value).IsRequired();

        builder.HasIndex(t => new { t.GameId, t.Value }).IsUnique();
    }
}
