using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Games;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class CatalogSeedHistoryConfiguration : IEntityTypeConfiguration<CatalogSeedHistory>
{
    public void Configure(EntityTypeBuilder<CatalogSeedHistory> builder)
    {
        builder.ToTable("catalog_seed_history");

        builder.HasKey(h => h.Id);
        builder.Property(h => h.ManifestVersion).IsRequired();
        builder.Property(h => h.Checksum).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(h => h.AppliedAtUtc).IsRequired();

        builder.HasIndex(h => h.ManifestVersion).IsUnique();
    }
}
