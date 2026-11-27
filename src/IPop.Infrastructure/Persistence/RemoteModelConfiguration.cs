using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IPop.Infrastructure.Persistence;

/// Remote help (BIGIS #8 v3) model parts kept out of IPopDbContext.cs (already over 1600 lines).
public static class RemoteModelConfiguration
{
    public static void Apply(ModelBuilder b)
    {
        b.ApplyConfiguration(new RemoteAuditEntryConfiguration());
        b.ApplyConfiguration(new RemoteMachineSettingsConfiguration());
        b.Entity<NotifierDevice>().Property(x => x.Monitors).HasMaxLength(200);
        b.Entity<RemoteSession>().Property(x => x.EndedBy).HasMaxLength(16);
    }
}

public sealed class RemoteAuditEntryConfiguration : IEntityTypeConfiguration<RemoteAuditEntry>
{
    public void Configure(EntityTypeBuilder<RemoteAuditEntry> e)
    {
        e.ToTable("RemoteAuditEntries");
        e.Property(x => x.What).HasMaxLength(200);
        e.HasIndex(x => x.At);
        e.HasIndex(x => new { x.MachineId, x.At });
        e.HasOne<RemoteMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// The per-PC Admin settings added to <see cref="RemoteMachine"/> in v3.
public sealed class RemoteMachineSettingsConfiguration : IEntityTypeConfiguration<RemoteMachine>
{
    public void Configure(EntityTypeBuilder<RemoteMachine> e)
    {
        e.Property(x => x.Alias).HasMaxLength(40);
        e.Property(x => x.Group).HasMaxLength(60);
        e.Property(x => x.UnattendedPermissions).HasMaxLength(64);
        e.Property(x => x.AllowedPermissions).HasMaxLength(64);
    }
}
