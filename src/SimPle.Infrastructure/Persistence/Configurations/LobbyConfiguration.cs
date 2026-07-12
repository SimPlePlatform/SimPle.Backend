using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class LobbyConfiguration : IEntityTypeConfiguration<Lobby>
{
    public void Configure(EntityTypeBuilder<Lobby> builder)
    {
        builder.ToTable("lobbies", t =>
        {
            t.HasCheckConstraint("ck_lobbies_max_players", "\"MaxPlayers\" >= 2 AND \"MaxPlayers\" <= 8");
            t.HasCheckConstraint("ck_lobbies_capability_version", "\"CapabilityVersion\" >= 1");
            t.HasCheckConstraint("ck_lobbies_revision", "\"Revision\" >= 1");

            // A terminal lobby must carry a reason, and a live one must not — the reason is the audit trail for
            // why a lobby stopped existing, so an unexplained Closed row is a bug, not a valid state.
            t.HasCheckConstraint(
                "ck_lobbies_closed_reason_iff_terminal",
                "(\"State\" IN ('Closed', 'Expired')) = (\"ClosedReason\" IS NOT NULL)");
        });

        builder.HasKey(l => l.Id);

        // Computed projections over the aggregate, not persisted state. JoinedMembers in particular MUST be
        // ignored: EF's relationship-discovery convention sees an IEnumerable<LobbyMember> property and creates a
        // *second* Lobby->LobbyMember relationship behind a shadow FK ("LobbyId1"), which would ship a dead,
        // always-null duplicate foreign key column alongside the real one.
        builder.Ignore(l => l.JoinedMembers);
        builder.Ignore(l => l.CurrentSettings);

        builder.Property(l => l.GameSlug).IsRequired().HasMaxLength(64);
        builder.Property(l => l.CapabilityVersion).IsRequired();
        builder.Property(l => l.HostUserId).IsRequired();
        builder.Property(l => l.Privacy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.MaxPlayers).IsRequired();
        builder.Property(l => l.TimeControlId).IsRequired().HasMaxLength(32);
        builder.Property(l => l.Rated).IsRequired();
        builder.Property(l => l.ResolvedRegion).IsRequired().HasMaxLength(32);
        builder.Property(l => l.SpectatorPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.TieBreakRuleId).IsRequired().HasMaxLength(32);
        builder.Property(l => l.AiFillRequested).IsRequired();
        builder.Property(l => l.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(l => l.Revision).IsRequired().HasDefaultValue(1);
        builder.Property(l => l.ExpiresAtUtc).IsRequired();
        builder.Property(l => l.ClosedReason).HasConversion<string>().HasMaxLength(24);
        builder.Property(l => l.CorrelationId).IsRequired();

        // Npgsql row-version pattern: uint property mapped to xmin (optimistic concurrency token).
        builder.Property(l => l.Version).IsRowVersion();

        // Public-discovery keyset index. Partial, so it stays small and — more importantly — so a private lobby is
        // not even present in the structure discovery pages over.
        builder.HasIndex(l => new { l.CreatedAt, l.Id })
            .HasFilter("\"State\" = 'Open' AND \"Privacy\" = 'Public'")
            .HasDatabaseName("ix_lobbies_public_discovery");

        // Expiry sweep: find open lobbies past their deadline.
        builder.HasIndex(l => l.ExpiresAtUtc)
            .HasFilter("\"State\" IN ('Open', 'Starting')")
            .HasDatabaseName("ix_lobbies_expiry_sweep");

        builder.HasIndex(l => l.HostUserId).HasDatabaseName("ix_lobbies_host");

        builder.HasMany(l => l.Members)
            .WithOne()
            .HasForeignKey(m => m.LobbyId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Members).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<User>().WithMany().HasForeignKey(l => l.HostUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LobbyMemberConfiguration : IEntityTypeConfiguration<LobbyMember>
{
    public void Configure(EntityTypeBuilder<LobbyMember> builder)
    {
        builder.ToTable("lobby_members", t =>
        {
            // A departed member must have a departure time; a seated one must not.
            t.HasCheckConstraint(
                "ck_lobby_members_left_at_iff_terminal",
                "(\"State\" IN ('Left', 'Kicked')) = (\"LeftAtUtc\" IS NOT NULL)");

            // Only a kick records who did it.
            t.HasCheckConstraint(
                "ck_lobby_members_removed_by_only_on_kick",
                "\"RemovedByUserId\" IS NULL OR \"State\" = 'Kicked'");
        });

        builder.HasKey(m => m.Id);

        // Ids come from Entity's field initializer (Guid.NewGuid()), never from the store. EF must be told this:
        // by convention it treats a Guid key as store-generated, and then infers the state of an entity added to a
        // *tracked* parent's collection from its key — a non-default Guid reads as "this row already exists", so a
        // brand-new member is emitted as an UPDATE against a row that is not there (0 rows affected -> a spurious
        // DbUpdateConcurrencyException) instead of an INSERT.
        //
        // Lobby is the first aggregate in this codebase with a *mutable* child collection; Module 4's GameTags are
        // only ever written while the parent itself is Added, which is why nothing hit this before.
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.LobbyId).IsRequired();
        builder.Property(m => m.UserId).IsRequired();
        builder.Property(m => m.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.IsReady).IsRequired();
        builder.Property(m => m.JoinedAtUtc).IsRequired();
        builder.Property(m => m.LeftAtUtc);
        builder.Property(m => m.RemovedByUserId);

        // THE cross-lobby invariant: one joined seat per user across the whole platform. An application-level check
        // loses this race; only the filtered unique index actually enforces it, and it is what the concurrent
        // last-seat-join test asserts against.
        //
        // It cannot see the tickets table, so "one active lobby OR one active ticket" is only half-enforced here —
        // the other half must run inside the same transaction that inserts (brief Risk #2).
        builder.HasIndex(m => m.UserId)
            .IsUnique()
            .HasFilter("\"State\" = 'Joined'")
            .HasDatabaseName("ux_lobby_members_one_joined_per_user");

        // Roster reads, and the tenure order that drives host transfer.
        builder.HasIndex(m => new { m.LobbyId, m.JoinedAtUtc, m.UserId })
            .HasDatabaseName("ix_lobby_members_lobby_tenure");

        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
