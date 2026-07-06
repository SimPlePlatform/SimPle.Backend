using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> builder)
    {
        builder.ToTable("outbox_deliveries");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.HandlerName).HasMaxLength(128).IsRequired();
        builder.Property(d => d.AttemptCount).IsRequired();
        builder.Property(d => d.Processed).IsRequired();
        builder.Property(d => d.DeadLettered).IsRequired();
        builder.Property(d => d.LastError).HasMaxLength(512);

        // One delivery row per (event, handler); no global processed marker.
        builder.HasIndex(d => new { d.EventId, d.HandlerName })
            .IsUnique()
            .HasDatabaseName("ix_outbox_deliveries_event_handler");

        // Dispatcher lookup for unprocessed / dead-lettered work per handler.
        builder.HasIndex(d => new { d.HandlerName, d.Processed, d.DeadLettered })
            .HasDatabaseName("ix_outbox_deliveries_handler_processed_dead");

        builder.HasOne<OutboxMessage>()
            .WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
