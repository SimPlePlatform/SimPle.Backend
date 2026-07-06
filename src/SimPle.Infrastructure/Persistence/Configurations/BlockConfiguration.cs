using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimPle.Domain.Friends;
using SimPle.Domain.Users;

namespace SimPle.Infrastructure.Persistence.Configurations;

public sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> builder)
    {
        builder.ToTable("blocks", t =>
        {
            t.HasCheckConstraint("ck_no_self_block", @"""BlockerId"" != ""BlockedId""");
        });

        builder.HasKey(b => b.Id);
        builder.HasIndex(b => b.BlockerId);
        builder.HasIndex(b => b.BlockedId);
        builder.HasIndex(b => new { b.BlockerId, b.BlockedId }).IsUnique();

        // No IsRowVersion — blocks are add/remove only, no state transitions
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.BlockerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.BlockedId).OnDelete(DeleteBehavior.Cascade);
    }
}
