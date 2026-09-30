using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IPop.Infrastructure.Persistence;

public sealed class SearchHistoryEntryConfiguration : IEntityTypeConfiguration<SearchHistoryEntry>
{
    public void Configure(EntityTypeBuilder<SearchHistoryEntry> entry)
    {
        entry.Property(x => x.Query).HasMaxLength(200);
        entry.Property(x => x.NormalizedQuery).HasMaxLength(200);
        entry.HasIndex(x => new { x.UserId, x.NormalizedQuery }).IsUnique();
        entry.HasIndex(x => new { x.UserId, x.LastSearchedAt });
        entry.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
