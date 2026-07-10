using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Games;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class GameModeCapabilityConfiguration : IEntityTypeConfiguration<GameModeCapability>
{
    public void Configure(EntityTypeBuilder<GameModeCapability> builder)
    {
        builder.ToTable("game_mode_capabilities");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Mode).IsRequired();

        builder.HasIndex(c => new { c.GameId, c.Mode }).IsUnique();
    }
}
