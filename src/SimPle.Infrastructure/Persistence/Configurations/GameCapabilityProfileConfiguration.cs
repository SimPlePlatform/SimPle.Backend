using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Games;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class GameCapabilityProfileConfiguration : IEntityTypeConfiguration<GameCapabilityProfile>
{
    public void Configure(EntityTypeBuilder<GameCapabilityProfile> builder)
    {
        builder.ToTable("game_capability_profiles", t =>
        {
            t.HasCheckConstraint("ck_game_capability_profiles_version", "\"CapabilityVersion\" >= 1");
            t.HasCheckConstraint(
                "ck_game_capability_profiles_players",
                "\"MinPlayers\" >= 2 AND \"MinPlayers\" <= \"MaxPlayers\" AND \"MaxPlayers\" <= 8");
            t.HasCheckConstraint("ck_game_capability_profiles_modes_nonempty", "cardinality(\"AllowedModes\") > 0");
            t.HasCheckConstraint("ck_game_capability_profiles_time_controls_nonempty", "cardinality(\"TimeControls\") > 0");
            t.HasCheckConstraint("ck_game_capability_profiles_tie_breaks_nonempty", "cardinality(\"TieBreakRules\") > 0");
            t.HasCheckConstraint("ck_game_capability_profiles_spectators_nonempty", "cardinality(\"SpectatorPolicies\") > 0");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.GameSlug).IsRequired().HasMaxLength(64);
        builder.Property(p => p.CapabilityVersion).IsRequired();
        builder.Property(p => p.MinPlayers).IsRequired();
        builder.Property(p => p.MaxPlayers).IsRequired();

        // Npgsql maps List<string> to text[] natively — no junction tables needed for these small, read-mostly
        // allow-lists, and the whole profile stays a single row read.
        builder.Property(p => p.AllowedModes).IsRequired();
        builder.Property(p => p.TimeControls).IsRequired();
        builder.Property(p => p.TieBreakRules).IsRequired();
        builder.Property(p => p.SpectatorPolicies).IsRequired();

        builder.Property(p => p.RatedEligible).IsRequired();
        builder.Property(p => p.AiFillEligible).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.ManifestVersion).IsRequired().HasMaxLength(32);

        // The pin. A lobby/ticket stores (GameSlug, CapabilityVersion) and resolves it here.
        builder.HasIndex(p => new { p.GameSlug, p.CapabilityVersion })
            .IsUnique()
            .HasDatabaseName("ux_game_capability_profiles_pin");

        // "The current profile for this game" — at most one active version per game, so a create command never has
        // to choose between two live profiles.
        builder.HasIndex(p => p.GameSlug)
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("ux_game_capability_profiles_one_active_per_game");

        // FK to M4's games.Slug (its alternate key). Restrict, not cascade: retiring a game must not silently
        // delete the capability profile that pinned lobbies still resolve through.
        builder.HasOne<Game>()
            .WithMany()
            .HasForeignKey(p => p.GameSlug)
            .HasPrincipalKey(g => g.Slug)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CapabilitySeedHistoryConfiguration : IEntityTypeConfiguration<CapabilitySeedHistory>
{
    public void Configure(EntityTypeBuilder<CapabilitySeedHistory> builder)
    {
        builder.ToTable("capability_seed_history");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.ManifestVersion).IsRequired().HasMaxLength(32);
        builder.Property(h => h.Checksum).IsRequired().HasMaxLength(64);
        builder.Property(h => h.AppliedAtUtc).IsRequired();

        builder.HasIndex(h => h.ManifestVersion).IsUnique();
    }
}
