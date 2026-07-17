using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Chat;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class ChatMessageHoldConfiguration : IEntityTypeConfiguration<ChatMessageHold>
{
    public void Configure(EntityTypeBuilder<ChatMessageHold> builder)
    {
        builder.ToTable("chat_message_holds");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.MessageId).IsRequired();
        builder.Property(h => h.ReasonCode).HasMaxLength(64).IsRequired();
        builder.Property(h => h.PlacedAtUtc).IsRequired();
        builder.Property(h => h.ReleasedAtUtc);
        builder.Property(h => h.AcknowledgedAtUtc);

        // Partial index: the retention cleanup sweep's NOT EXISTS(active hold) probe.
        builder.HasIndex(h => h.MessageId)
            .HasFilter("\"ReleasedAtUtc\" IS NULL")
            .HasDatabaseName("ix_chat_message_holds_active");

        // RESTRICT: a held message can never be swept away by the retention cleanup while evidence exists.
        builder.HasOne<ChatMessage>().WithMany().HasForeignKey(h => h.MessageId).OnDelete(DeleteBehavior.Restrict);
    }
}
