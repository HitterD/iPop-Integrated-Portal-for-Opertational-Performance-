using IPop.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Persistence;

public class IPopDbContext(DbContextOptions<IPopDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<OrgNode> OrgNodes => Set<OrgNode>();
    public DbSet<UserOrgNode> UserOrgNodes => Set<UserOrgNode>();
    public DbSet<DirGroup> DirGroups => Set<DirGroup>();
    public DbSet<UserDirGroup> UserDirGroups => Set<UserDirGroup>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<HrisSyncState> HrisSyncState => Set<HrisSyncState>();
    public DbSet<HrisSyncRun> HrisSyncRuns => Set<HrisSyncRun>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<SynologyCredential> SynologyCredentials => Set<SynologyCredential>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Site>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<Division>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Nik).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasOne(x => x.Department).WithMany(d => d.Members).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Division).WithMany().HasForeignKey(x => x.DivisionId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<AuthSession>(e =>
        {
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<HrisSyncState>().HasData(new HrisSyncState { Id = 1 });
        b.Entity<HrisSyncRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedAt);
        });
        b.Entity<AuditLog>(e => e.HasIndex(x => x.CreatedAt));
        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentRoleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Permission>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.PermissionId, x.DepartmentCode });
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserPermissionOverride>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PermissionId, x.DepartmentCode });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany(p => p.UserOverrides).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SynologyCredential>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Server });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrgNode>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<UserOrgNode>(e =>
        {
            e.HasKey(x => new { x.UserId, x.OrgNodeId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.OrgNode).WithMany(n => n.Members).HasForeignKey(x => x.OrgNodeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DirGroup>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UserDirGroup>(e =>
        {
            e.HasKey(x => new { x.UserId, x.DirGroupId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DirGroup).WithMany(g => g.Members).HasForeignKey(x => x.DirGroupId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
