using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Division admin behavior — same seam as DirectoryTreeTests: SQLite in-memory via TestDb,
/// asserting persisted state. Guard/audit tests call the production helpers directly
/// (InternalsVisibleTo) since there is no WebApplicationFactory harness.
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
    public async Task Guard_Accepts_ActiveDepartmentAndSite()
    {
        using var t = new TestDb();
        SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "x" };
        var err = await AdminEndpoints.ApplyDivisionParents(div, "D1", "S1", t.Db, default);

        Assert.Null(err);                         // no error — parents accepted
        var dept = await t.Db.Departments.SingleAsync();
        var site = await t.Db.Sites.SingleAsync();
        Assert.Equal(dept.Id, div.DepartmentId);
        Assert.Equal(site.Id, div.SiteId);
    }

    [Fact]
    public async Task Guard_Rejects_InactiveDepartment()
    {
        using var t = new TestDb();
        SeedDeptSite(t, deptActive: false);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "x" };
        var err = await AdminEndpoints.ApplyDivisionParents(div, "D1", null, t.Db, default);

        Assert.NotNull(err);
        var bad = Assert.IsAssignableFrom<IStatusCodeHttpResult>(err);
        Assert.Equal(400, bad.StatusCode);
        Assert.Null(div.DepartmentId);            // rejected — parent not set
    }

    [Fact]
    public async Task Guard_Rejects_InactiveSite()
    {
        using var t = new TestDb();
        SeedDeptSite(t, siteActive: false);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "x" };
        var err = await AdminEndpoints.ApplyDivisionParents(div, null, "S1", t.Db, default);

        Assert.NotNull(err);
        var bad = Assert.IsAssignableFrom<IStatusCodeHttpResult>(err);
        Assert.Equal(400, bad.StatusCode);
        Assert.Null(div.SiteId);                 // rejected — parent not set
    }

    [Fact]
    public async Task Guard_Rejects_UnknownDepartment()
    {
        using var t = new TestDb();
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "x" };
        var err = await AdminEndpoints.ApplyDivisionParents(div, "NOPE", null, t.Db, default);
        Assert.NotNull(err);
        Assert.Null(div.DepartmentId);
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

    // Patch-to-inactive must clear member DivisionId the same way Delete does — otherwise a
    // deactivated Division's members keep a non-null DivisionId pointing nowhere-visible and
    // silently drop out of both the /structure tree and NoDivisionCount. Replicates the exact
    // PATCH handler logic (deactivating flag + conditional ExecuteUpdateAsync) since there is no
    // WebApplicationFactory harness in this repo (see class doc comment).
    [Fact]
    public async Task Patch_Division_Deactivate_ClearsMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        // PATCH body: { IsActive: false } — mirrors org.MapPatch("/divisions/{code}") in AdminEndpoints.
        bool? requestedIsActive = false;
        var loaded = await t.Db.Divisions.FirstAsync(d => d.Code == "DV1");
        var deactivating = requestedIsActive is false && loaded.IsActive;
        if (requestedIsActive is { } active) loaded.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.DivisionId == loaded.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Null(check.DivisionId);
        Assert.False((await t.Verify().Divisions.FirstAsync(d => d.Code == "DV1")).IsActive);
    }

    [Fact]
    public async Task Patch_Division_NameOnly_DoesNotClearMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Old",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        // PATCH body: { Name: "New" } — IsActive left unspecified (null), so deactivating is false.
        bool? requestedIsActive = null;
        var loaded = await t.Db.Divisions.FirstAsync(d => d.Code == "DV1");
        loaded.Name = "New";
        var deactivating = requestedIsActive is false && loaded.IsActive;
        if (requestedIsActive is { } active) loaded.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.DivisionId == loaded.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);   // untouched
    }

    [Fact]
    public async Task Patch_Division_Reactivate_DoesNotClearMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId, IsActive = false };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        // PATCH body: { IsActive: true } — reactivation, not deactivation.
        bool? requestedIsActive = true;
        var loaded = await t.Db.Divisions.FirstAsync(d => d.Code == "DV1");
        var deactivating = requestedIsActive is false && loaded.IsActive;
        if (requestedIsActive is { } active) loaded.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.DivisionId == loaded.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);   // untouched
        Assert.True((await t.Verify().Divisions.FirstAsync(d => d.Code == "DV1")).IsActive);
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

    // Exercises the production Audit helper (InternalsVisibleTo) — asserts the audit row
    // a Division mutation actually writes, including Entity = "Division".
    [Fact]
    public async Task Audit_Writes_DivisionEntity_ForDivisionMutation()
    {
        using var t = new TestDb();
        AdminEndpoints.Audit(t.Db, "admin", "admin.org.division.create", "DV1", DateTime.UtcNow, "Division");
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var entry = await t.Verify().AuditLogs.AsNoTracking()
            .FirstAsync(a => a.Action == "admin.org.division.create");
        Assert.Equal("DV1", entry.EntityId);
        Assert.Equal("admin", entry.ActorNik);
        Assert.Equal("Division", entry.Entity);
        Assert.Equal("{}", entry.DetailJson);
    }

    // Guards the default-entity contract: the helper still defaults to "User" for the
    // existing user-mutation call sites that do not pass an entity.
    [Fact]
    public async Task Audit_Defaults_To_UserEntity_WhenEntityNotProvided()
    {
        using var t = new TestDb();
        AdminEndpoints.Audit(t.Db, "admin", "admin.user.edit", "20080115", DateTime.UtcNow);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var entry = await t.Verify().AuditLogs.AsNoTracking()
            .FirstAsync(a => a.Action == "admin.user.edit");
        Assert.Equal("User", entry.Entity);
    }

    // ---- Finding 1: Department/Site deactivation must cascade to descendant Divisions ----

    [Fact]
    public async Task Patch_Department_Deactivate_ClearsMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        bool? requestedIsActive = false;
        var dept = await t.Db.Departments.FirstAsync(d => d.Code == "D1");
        var deactivating = requestedIsActive is false && dept.IsActive;
        if (requestedIsActive is { } active) dept.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.Division != null && u.Division.DepartmentId == dept.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        Assert.Null((await t.Verify().Users.FirstAsync(u => u.Nik == "20080115")).DivisionId);
        Assert.False((await t.Verify().Departments.FirstAsync(d => d.Code == "D1")).IsActive);
    }

    [Fact]
    public async Task Patch_Site_Deactivate_ClearsMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        bool? requestedIsActive = false;
        var site = await t.Db.Sites.FirstAsync(s => s.Code == "S1");
        var deactivating = requestedIsActive is false && site.IsActive;
        if (requestedIsActive is { } active) site.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.Division != null && u.Division.SiteId == site.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        Assert.Null((await t.Verify().Users.FirstAsync(u => u.Nik == "20080115")).DivisionId);
        Assert.False((await t.Verify().Sites.FirstAsync(s => s.Code == "S1")).IsActive);
    }

    [Fact]
    public async Task Patch_Department_NameOnly_DoesNotClearMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Old",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        bool? requestedIsActive = null;
        var dept = await t.Db.Departments.FirstAsync(d => d.Code == "D1");
        dept.Name = "NewName";
        var deactivating = requestedIsActive is false && dept.IsActive;
        if (requestedIsActive is { } active) dept.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.Division != null && u.Division.DepartmentId == dept.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        Assert.Equal(div.Id, (await t.Verify().Users.FirstAsync(u => u.Nik == "20080115")).DivisionId);
    }

    [Fact]
    public async Task Patch_Site_NameOnly_DoesNotClearMemberDivisionId()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Old",
            DepartmentId = deptId, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DivisionId = div.Id;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        bool? requestedIsActive = null;
        var site = await t.Db.Sites.FirstAsync(s => s.Code == "S1");
        site.Name = "NewName";
        var deactivating = requestedIsActive is false && site.IsActive;
        if (requestedIsActive is { } active) site.IsActive = active;
        if (deactivating)
            await t.Db.Users.Where(u => u.Division != null && u.Division.SiteId == site.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null));
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        Assert.Equal(div.Id, (await t.Verify().Users.FirstAsync(u => u.Nik == "20080115")).DivisionId);
    }

    // ---- Finding 2: parentless Division assignment must not null existing Site/Department ----

    [Fact]
    public async Task Assign_User_Division_Parentless_NoDepartment_PreservesExistingDepartment()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "NoDept",
            DepartmentId = null, SiteId = siteId, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DepartmentId = deptId;
        user.SiteId = siteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        var d = await t.Db.Divisions.SingleAsync(x => x.Code == "DV1" && x.IsActive);
        loaded.DivisionId = d.Id;
        if (d.DepartmentId is not null) loaded.DepartmentId = d.DepartmentId;
        if (d.SiteId is not null) loaded.SiteId = d.SiteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);
        Assert.Equal(deptId, check.DepartmentId); // preserved — division had no department
        Assert.Equal(siteId, check.SiteId);
    }

    [Fact]
    public async Task Assign_User_Division_Parentless_NoSite_PreservesExistingSite()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV2", Name = "NoSite",
            DepartmentId = deptId, SiteId = null, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DepartmentId = deptId;
        user.SiteId = siteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        var d = await t.Db.Divisions.SingleAsync(x => x.Code == "DV2" && x.IsActive);
        loaded.DivisionId = d.Id;
        if (d.DepartmentId is not null) loaded.DepartmentId = d.DepartmentId;
        if (d.SiteId is not null) loaded.SiteId = d.SiteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);
        Assert.Equal(deptId, check.DepartmentId);
        Assert.Equal(siteId, check.SiteId); // preserved — division had no site
    }

    [Fact]
    public async Task Assign_User_Division_Parentless_NoParents_PreservesBoth()
    {
        using var t = new TestDb();
        var (deptId, siteId) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV3", Name = "Orphan",
            DepartmentId = null, SiteId = null, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DepartmentId = deptId;
        user.SiteId = siteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        var d = await t.Db.Divisions.SingleAsync(x => x.Code == "DV3" && x.IsActive);
        loaded.DivisionId = d.Id;
        if (d.DepartmentId is not null) loaded.DepartmentId = d.DepartmentId;
        if (d.SiteId is not null) loaded.SiteId = d.SiteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);
        Assert.Equal(deptId, check.DepartmentId);
        Assert.Equal(siteId, check.SiteId);
    }

    [Fact]
    public async Task Assign_User_Division_BothParents_OverwritesBoth()
    {
        using var t = new TestDb();
        var (deptId1, siteId1) = SeedDeptSite(t);
        var dept2 = new Department { Id = Guid.NewGuid(), Code = "D2", Name = "Ops", IsActive = true };
        var site2 = new Site { Id = Guid.NewGuid(), Code = "S2", Name = "Branch", IsActive = true };
        t.Db.Departments.Add(dept2);
        t.Db.Sites.Add(site2);
        await t.Db.SaveChangesAsync();
        var div = new Division { Id = Guid.NewGuid(), Code = "DV4", Name = "Ops@Branch",
            DepartmentId = dept2.Id, SiteId = site2.Id, IsActive = true };
        t.Db.Divisions.Add(div);
        var user = t.AddUser("20080115");
        user.DepartmentId = deptId1;
        user.SiteId = siteId1;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var loaded = await t.Db.Users.FirstAsync(u => u.Nik == "20080115");
        var d = await t.Db.Divisions.SingleAsync(x => x.Code == "DV4" && x.IsActive);
        loaded.DivisionId = d.Id;
        if (d.DepartmentId is not null) loaded.DepartmentId = d.DepartmentId;
        if (d.SiteId is not null) loaded.SiteId = d.SiteId;
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();

        var check = await t.Verify().Users.FirstAsync(u => u.Nik == "20080115");
        Assert.Equal(div.Id, check.DivisionId);
        Assert.Equal(dept2.Id, check.DepartmentId);
        Assert.Equal(site2.Id, check.SiteId);
    }
}
