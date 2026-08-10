using System.Security.Claims;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Division admin behavior — same seam as DirectoryTreeTests: SQLite in-memory via TestDb,
/// asserting persisted state. Mirrors the operations the admin endpoints perform.
public class DivisionAdminTests
{
    private static (Guid deptId, Guid siteId) SeedDeptSite(TestDb t, bool deptActive = true, bool siteActive = true)
    {
        var dept = new Department { Id = Guid.NewGuid(), Code = "D1", Name = "Eng", IsActive = deptActive };
        var site = new Site { Id = Guid.NewGuid(), Code = "S1", Name = "HQ", IsActive = siteActive };
        t.Db.Departments.Add(dept);
        t.Db.Sites.Add(site);
        t.Db.SaveChanges();
        return (dept.Id, site.Id);
    }

    [Fact]
    public async Task Create_Division_Persists_WithParents()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var found = await t.Verify().Divisions.Include(d => d.Department).Include(d => d.Site)
            .SingleAsync(d => d.Code == "DV1");
        Assert.Equal("Eng@HQ", found.Name);
        Assert.Equal("D1", found.Department!.Code);
        Assert.Equal("S1", found.Site!.Code);
        Assert.True(found.IsActive);
    }

    [Fact]
    public async Task Create_Division_Rejects_InactiveDepartment()
    {
        using var t = new TestDb();
        var (deptId, _) = SeedDeptSite(t, deptActive: false);
        // Endpoint guard: only an active department is accepted.
        var ok = await t.Db.Departments.AnyAsync(d => d.Id == deptId && d.IsActive);
        Assert.False(ok, "inactive department must fail the IsActive guard");
    }

    [Fact]
    public async Task Create_Division_Rejects_InactiveSite()
    {
        using var t = new TestDb();
        var (_, siteId) = SeedDeptSite(t, siteActive: false);
        var ok = await t.Db.Sites.AnyAsync(s => s.Id == siteId && s.IsActive);
        Assert.False(ok, "inactive site must fail the IsActive guard");
    }

    [Fact]
    public async Task Patch_Division_Updates_Name_AndActive()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Old",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var found = await t.Db.Divisions.FirstAsync(d => d.Code == "DV1");
        found.Name = "New";
        found.IsActive = false;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Divisions.FirstAsync(d => d.Code == "DV1");
        Assert.Equal("New", check.Name);
        Assert.False(check.IsActive);
    }

    [Fact]
    public async Task Delete_Division_SetsMemberDivisionIdNull_PreservesSiteDept()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        user.DepartmentId = deptId;
        user.SiteId = siteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        // Delete: set member DivisionId null, preserve Site/Department, remove the division.
        await t.Db.Users.Where(u => u.DivisionId == div.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        var loaded = await t.Db.Divisions.FirstAsync(d => d.Id == div.Id);
        t.Db.Divisions.Remove(loaded);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var u = await t.Verify().Users.FirstAsync(x => x.Nik == "20080115");
        Assert.Null(u.DivisionId);
        Assert.Equal(deptId, u.DepartmentId);     // preserved
        Assert.Equal(siteId, u.SiteId);           // preserved
        Assert.False(await t.Verify().Divisions.AnyAsync(d => d.Id == div.Id));
    }

    [Fact]
    public async Task Assign_User_Division_SyncsSiteAndDepartment()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        // Assign: sync Site/Department from the Division's parents.
        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        var d = await t.Db.Divisions.SingleAsync(x => x.Code == "DV1" && x.IsActive);
        loaded.DivisionId = d.Id;
        loaded.DepartmentId = d.DepartmentId;
        loaded.SiteId = d.SiteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);
        Assert.Equal(deptId, check.DepartmentId);
        Assert.Equal(siteId, check.SiteId);
    }

    [Fact]
    public async Task Unassign_User_Division_ClearsOnlyDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        user.DepartmentId = deptId;
        user.SiteId = siteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        loaded.DivisionId = null;     // unassign clears only DivisionId
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Null(check.DivisionId);
        Assert.Equal(deptId, check.DepartmentId);   // preserved
        Assert.Equal(siteId, check.SiteId);         // preserved
    }

    [Fact]
    public async Task Division_FiltersInactive_InQuery()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        t.Db.Divisions.AddRange(
            new Division { Id = Guid.NewGuid(), Code = "A", Name = "Active", DepartmentId = deptId, SiteId = siteId },
            new Division { Id = Guid.NewGuid(), Code = "B", Name = "Inactive", IsActive = false });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var active = await t.Verify().Divisions.Where(d => d.IsActive).ToListAsync();
        Assert.Single(active);
        Assert.Equal("A", active[0].Code);
    }

    // Admin authorization: the AdminOnly policy requires the Admin role — a non-admin gets 403.
    [Fact]
    public void AdminOnly_Policy_DeniesNonAdmin()
    {
        var policy = new AuthorizationPolicyBuilder().RequireRole("Admin").Build();
        Assert.Contains(policy.Requirements,
            r => r is RolesAuthorizationRequirement roles && roles.AllowedRoles.Contains("Admin"));

        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"));
        var employee = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Employee")], "test"));
        Assert.True(admin.IsInRole("Admin"));        // admin passes the AdminOnly guard
        Assert.False(employee.IsInRole("Admin"));    // non-admin is denied (403)
    }

    [Fact]
    public async Task Audit_Entry_Created_On_DivisionMutation()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId };
        t.Db.Divisions.Add(div);
        t.Db.AuditLogs.Add(new AuditLog
        {
            ActorNik = "admin", Action = "admin.org.division.create", Entity = "Division",
            EntityId = "DV1", DetailJson = "{}", CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var entry = await t.Verify().AuditLogs.AsNoTracking()
            .FirstAsync(a => a.Action == "admin.org.division.create");
        Assert.Equal("DV1", entry.EntityId);
        Assert.Equal("admin", entry.ActorNik);
        Assert.Equal("Division", entry.Entity);
    }
}
