using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Chat;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Scope).IsRequired();
        builder.Property(m => m.ScopeId).IsRequired();
        builder.Property(m => m.SenderId).IsRequired();
        builder.Property(m => m.Body).HasColumnType("text").IsRequired();
        builder.Property(m => m.SchemaVersion).HasDefaultValue(1).IsRequired();
        builder.Property(m => m.ClientCommandId).IsRequired();
        builder.Property(m => m.DeletedAtUtc);
        builder.Property(m => m.DeletedByUserId);
        builder.Property(m => m.RetainUntilUtc).IsRequired();

        // A duplicate send (client retry) catches 23505 here and the caller re-reads the original message.
        builder.HasIndex(m => new { m.SenderId, m.ClientCommandId })
            .IsUnique()
            .HasDatabaseName("ux_chat_messages_sender_command");

        // History cursor. Scope-prefixed so Module 8 (Match scope) reuses this same index shape.
        builder.HasIndex(m => new { m.Scope, m.ScopeId, m.CreatedAt, m.Id })
            .HasDatabaseName("ix_chat_messages_scope_created_id");

        // Retention cleanup scan.
        builder.HasIndex(m => m.RetainUntilUtc).HasDatabaseName("ix_chat_messages_retain");

        // RESTRICT is the backstop: even if a future cleanup bug forgets the NOT EXISTS active-hold probe,
        // PostgreSQL refuses with 23503 rather than destroying moderation evidence tied to this sender.
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
    }
}
