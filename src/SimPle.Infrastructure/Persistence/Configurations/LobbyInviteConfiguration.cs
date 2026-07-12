using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Lobbies;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class LobbyInviteConfiguration : IEntityTypeConfiguration<LobbyInvite>
{
    public void Configure(EntityTypeBuilder<LobbyInvite> builder)
    {
        builder.ToTable("lobby_invites", t =>
        {
            t.HasCheckConstraint("ck_lobby_invites_no_self_invite", "\"InviterUserId\" <> \"InviteeUserId\"");
            t.HasCheckConstraint(
                "ck_lobby_invites_responded_iff_terminal",
                "(\"State\" <> 'Pending') = (\"RespondedAtUtc\" IS NOT NULL)");
        });

        builder.HasKey(i => i.Id);

        builder.Property(i => i.LobbyId).IsRequired();
        builder.Property(i => i.InviterUserId).IsRequired();
        builder.Property(i => i.InviteeUserId).IsRequired();
        builder.Property(i => i.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.ExpiresAtUtc).IsRequired();
        builder.Property(i => i.RespondedAtUtc);

        // At most one live invite per (lobby, invitee): re-inviting someone who already has a pending invite must
        // not mint a second one, or revoking would leave a live duplicate behind.
        builder.HasIndex(i => new { i.LobbyId, i.InviteeUserId })
            .IsUnique()
            .HasFilter("\"State\" = 'Pending'")
            .HasDatabaseName("ux_lobby_invites_one_pending_per_invitee");

        // "My pending invites" (dashboard) — the badge count and the list are this same bounded query.
        builder.HasIndex(i => new { i.InviteeUserId, i.CreatedAt, i.Id })
            .HasFilter("\"State\" = 'Pending'")
            .HasDatabaseName("ix_lobby_invites_invitee_pending");

        // Expiry sweep.
        builder.HasIndex(i => i.ExpiresAtUtc)
            .HasFilter("\"State\" = 'Pending'")
            .HasDatabaseName("ix_lobby_invites_expiry_sweep");

        builder.HasOne<Lobby>().WithMany().HasForeignKey(i => i.LobbyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.InviterUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.InviteeUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LobbyJoinCredentialConfiguration : IEntityTypeConfiguration<LobbyJoinCredential>
{
    public void Configure(EntityTypeBuilder<LobbyJoinCredential> builder)
    {
        builder.ToTable("lobby_join_credentials", t =>
        {
            t.HasCheckConstraint("ck_lobby_join_credentials_generation", "\"Generation\" >= 1");
            t.HasCheckConstraint(
                "ck_lobby_join_credentials_superseded_iff_terminal",
                "(\"State\" <> 'Active') = (\"SupersededAtUtc\" IS NOT NULL)");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.LobbyId).IsRequired();

        // Hex-encoded HMAC-SHA256 => exactly 64 characters. No plaintext column exists on this table by design.
        builder.Property(c => c.CodeDigest).IsRequired().HasMaxLength(64);
        builder.Property(c => c.LinkTokenDigest).IsRequired().HasMaxLength(64);

        builder.Property(c => c.Generation).IsRequired();
        builder.Property(c => c.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.SupersededAtUtc);

        // Global code uniqueness among live credentials — join-by-code looks up by digest alone, so two active
        // lobbies sharing a code would make the lookup ambiguous. The generator's bounded collision retry catches
        // the 23505 this raises.
        builder.HasIndex(c => c.CodeDigest)
            .IsUnique()
            .HasFilter("\"State\" = 'Active'")
            .HasDatabaseName("ux_lobby_join_credentials_active_code");

        builder.HasIndex(c => c.LinkTokenDigest)
            .IsUnique()
            .HasFilter("\"State\" = 'Active'")
            .HasDatabaseName("ux_lobby_join_credentials_active_link_token");

        // One active credential per lobby: rotation must supersede, never accumulate.
        builder.HasIndex(c => c.LobbyId)
            .IsUnique()
            .HasFilter("\"State\" = 'Active'")
            .HasDatabaseName("ux_lobby_join_credentials_one_active_per_lobby");

        builder.HasOne<Lobby>().WithMany().HasForeignKey(c => c.LobbyId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LobbyStartRequestConfiguration : IEntityTypeConfiguration<LobbyStartRequest>
{
    public void Configure(EntityTypeBuilder<LobbyStartRequest> builder)
    {
        builder.ToTable("lobby_start_requests", t =>
        {
            t.HasCheckConstraint("ck_lobby_start_requests_revision", "\"LobbyRevision\" >= 1");
            t.HasCheckConstraint(
                "ck_lobby_start_requests_resolved_iff_terminal",
                "(\"State\" <> 'Open') = (\"ResolvedAtUtc\" IS NOT NULL)");
            t.HasCheckConstraint(
                "ck_lobby_start_requests_failure_reason_only_on_failed",
                "\"FailureReason\" IS NULL OR \"State\" = 'Failed'");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.LobbyId).IsRequired();
        builder.Property(r => r.LobbyRevision).IsRequired();
        builder.Property(r => r.MatchRequestId).IsRequired();
        builder.Property(r => r.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.IdempotencyKey).IsRequired().HasMaxLength(128);
        builder.Property(r => r.CorrelationId).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(256);
        builder.Property(r => r.ResolvedAtUtc);

        // One open start-request per lobby revision. This is what makes a retried Start idempotent: the second
        // attempt at the same revision loses here rather than minting a second match request.
        builder.HasIndex(r => new { r.LobbyId, r.LobbyRevision })
            .IsUnique()
            .HasFilter("\"State\" = 'Open'")
            .HasDatabaseName("ux_lobby_start_requests_one_open_per_revision");

        // A client replaying the same idempotency key must replay, not re-run.
        builder.HasIndex(r => new { r.LobbyId, r.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_lobby_start_requests_idempotency");

        builder.HasIndex(r => r.MatchRequestId)
            .IsUnique()
            .HasDatabaseName("ux_lobby_start_requests_match_request");

        builder.HasOne<Lobby>().WithMany().HasForeignKey(r => r.LobbyId).OnDelete(DeleteBehavior.Cascade);
    }
}
