using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Matchmaking;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class MatchmakingTicketConfiguration : IEntityTypeConfiguration<MatchmakingTicket>
{
    public void Configure(EntityTypeBuilder<MatchmakingTicket> builder)
    {
        builder.ToTable("matchmaking_tickets", t =>
        {
            t.HasCheckConstraint("ck_matchmaking_tickets_player_count", "\"PlayerCount\" >= 2 AND \"PlayerCount\" <= 8");
            t.HasCheckConstraint("ck_matchmaking_tickets_capability_version", "\"CapabilityVersion\" >= 1");
            t.HasCheckConstraint("ck_matchmaking_tickets_retry_budget", "\"RetryBudget\" >= 0");
            t.HasCheckConstraint("ck_matchmaking_tickets_deadline_after_enqueue", "\"DeadlineAtUtc\" > \"EnqueuedAtUtc\"");

            // A *queued* ticket must not name a worker: a stale worker id on a queued row means a claim leaked and
            // two workers could believe they hold it. Terminal states deliberately KEEP the worker id — it is the
            // attribution behind the matchmaking-worker-failure observability signal, and clearing it would throw
            // away the only record of which worker resolved the ticket.
            t.HasCheckConstraint(
                "ck_matchmaking_tickets_no_worker_while_queued",
                "\"State\" <> 'Queued' OR \"ClaimedByWorker\" IS NULL");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.GameSlug).IsRequired().HasMaxLength(64);
        builder.Property(t => t.CapabilityVersion).IsRequired();
        builder.Property(t => t.Mode).IsRequired().HasMaxLength(32);
        builder.Property(t => t.PlayerCount).IsRequired();
        builder.Property(t => t.TimeControlId).IsRequired().HasMaxLength(32);
        builder.Property(t => t.Rated).IsRequired();
        builder.Property(t => t.ResolvedRegion).IsRequired().HasMaxLength(32);
        builder.Property(t => t.Rating).IsRequired();
        builder.Property(t => t.RatingSourceVersion).IsRequired().HasMaxLength(48);
        builder.Property(t => t.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.EnqueuedAtUtc).IsRequired();
        builder.Property(t => t.DeadlineAtUtc).IsRequired();
        builder.Property(t => t.RetryBudget).IsRequired();
        builder.Property(t => t.ClaimedByWorker).HasMaxLength(64);
        builder.Property(t => t.ClaimedAtUtc);
        builder.Property(t => t.ResolvedAtUtc);
        builder.Property(t => t.CorrelationId).IsRequired();

        builder.Property(t => t.Version).IsRowVersion();

        // One nonterminal ticket per user. 'Requeued' is included defensively: the domain's Requeue() returns the
        // ticket straight to 'Queued' and never persists 'Requeued' today, but if that ever changed, a ticket in
        // that state must still occupy the user's single slot rather than silently escaping this index.
        builder.HasIndex(t => t.UserId)
            .IsUnique()
            .HasFilter("\"State\" IN ('Queued', 'Claimed', 'Requeued')")
            .HasDatabaseName("ux_matchmaking_tickets_one_nonterminal_per_user");

        // The matching worker's candidate-pool scan: exact-match pool key, then oldest-first within it (the anchor
        // order). Partial so the index holds only what the worker actually scans.
        builder.HasIndex(t => new
            {
                t.GameSlug,
                t.CapabilityVersion,
                t.Mode,
                t.PlayerCount,
                t.TimeControlId,
                t.Rated,
                t.ResolvedRegion,
                t.EnqueuedAtUtc,
                t.Id,
            })
            .HasFilter("\"State\" = 'Queued'")
            .HasDatabaseName("ix_matchmaking_tickets_candidate_pool");

        // Expiry sweep.
        builder.HasIndex(t => t.DeadlineAtUtc)
            .HasFilter("\"State\" IN ('Queued', 'Claimed', 'Requeued')")
            .HasDatabaseName("ix_matchmaking_tickets_expiry_sweep");

        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MatchmakingAssignmentConfiguration : IEntityTypeConfiguration<MatchmakingAssignment>
{
    public void Configure(EntityTypeBuilder<MatchmakingAssignment> builder)
    {
        builder.ToTable("matchmaking_assignments", t =>
        {
            t.HasCheckConstraint(
                "ck_matchmaking_assignments_resolved_iff_terminal",
                "(\"State\" <> 'Active') = (\"ResolvedAtUtc\" IS NOT NULL)");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.TicketId).IsRequired();
        builder.Property(a => a.MatchRequestId).IsRequired();
        builder.Property(a => a.GroupId).IsRequired();
        builder.Property(a => a.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.Property(a => a.ResolvedAtUtc);

        // THE index that makes double-assignment impossible (brief Risk #1). FOR UPDATE SKIP LOCKED only stops two
        // workers from contending on the same row; a requeued ticket or a serialization retry can still attempt a
        // second assignment, and it is rejected here, not by the row lock. The two-worker real-Postgres test
        // asserts zero duplicates against exactly this index.
        builder.HasIndex(a => a.TicketId)
            .IsUnique()
            .HasFilter("\"State\" = 'Active'")
            .HasDatabaseName("ux_matchmaking_assignments_one_active_per_ticket");

        builder.HasIndex(a => a.GroupId).HasDatabaseName("ix_matchmaking_assignments_group");
        builder.HasIndex(a => a.MatchRequestId).HasDatabaseName("ix_matchmaking_assignments_match_request");

        builder.HasOne<MatchmakingTicket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}
