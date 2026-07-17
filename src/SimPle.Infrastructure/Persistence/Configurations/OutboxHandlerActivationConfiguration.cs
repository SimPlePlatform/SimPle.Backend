using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Outbox;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class OutboxHandlerActivationConfiguration : IEntityTypeConfiguration<OutboxHandlerActivation>
{
    public void Configure(EntityTypeBuilder<OutboxHandlerActivation> builder)
    {
        builder.ToTable("outbox_handler_activations");

        builder.HasKey(a => a.HandlerName);
        builder.Property(a => a.HandlerName).HasMaxLength(128).IsRequired();

        builder.Property(a => a.ActivatedAtUtc).IsRequired();
        builder.Property(a => a.WatermarkOccurredAtUtc).IsRequired();
        builder.Property(a => a.WatermarkEventId);
    }
}
