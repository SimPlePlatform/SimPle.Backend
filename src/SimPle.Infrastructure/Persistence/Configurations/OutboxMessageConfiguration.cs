using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.AggregateType).HasMaxLength(64).IsRequired();
        builder.Property(m => m.AggregateId).IsRequired();
        builder.Property(m => m.EventType).HasMaxLength(64).IsRequired();
        builder.Property(m => m.EventVersion).IsRequired();
        builder.Property(m => m.AggregateDomainVersion).IsRequired();
        builder.Property(m => m.RequestCycleId).IsRequired();
        builder.Property(m => m.OccurredAtUtc).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        // A retried transition cannot stage the same logical event twice.
        builder.HasIndex(m => new { m.AggregateId, m.EventType, m.AggregateDomainVersion })
            .IsUnique()
            .HasDatabaseName("ix_outbox_messages_aggregate_event_version");

        // Dispatch order + pending-outbox-age metric.
        builder.HasIndex(m => m.OccurredAtUtc).HasDatabaseName("ix_outbox_messages_occurredat");
    }
}
