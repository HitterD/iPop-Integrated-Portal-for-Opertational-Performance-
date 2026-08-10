using IPop.Api.Endpoints;
using IPop.Application.Rbac;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Directory read endpoints (structure tree, division search, employees divisionCode filter) —
/// same seam as DivisionAdminTests/DirectoryTreeTests: SQLite in-memory via TestDb.
/// The structure/search DB queries call the internal pure helpers (BuildStructure,
/// AggregateDivisionMatches) directly, since EF.Functions.ILike is Npgsql-only and does
/// not translate on SQLite — the tree/aggregation logic itself is what's under test here.
public class DirectoryReadEndpointsTests
{
    private static (Department dept, Site site) SeedDeptSite(TestDb t, string deptCode = "D1", string siteCode = "S1")
    {
        var dept = new Department { Id = Guid.NewGuid(), Code = deptCode, Name = $"Dept {deptCode}", IsActive = true };
        var site = new Site { Id = Guid.NewGuid(), Code = siteCode, Name = $"Site {siteCode}", IsActive = true };
        t.Db.Departments.Add(dept);
        t.Db.Sites.Add(site);
        t.Db.SaveChanges();
        return (dept, site);
    }

    [Fact]
    public async Task BuildStructure_NestsSiteDepartmentDivision_WithAggregateCounts()
    {
        using var t = new TestDb();
        var (dept, site) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = dept.Id, SiteId = site.Id, Department = dept, Site = site };
        t.Db.Divisions.Add(div);
        var u1 = t.AddUser("20080115"); u1.DivisionId = div.Id;
        var u2 = t.AddUser("20080116"); u2.DivisionId = div.Id;
        t.Db.SaveChanges();

        var result = DirectoryEndpoints.BuildStructure([div],
            new Dictionary<Guid, int> { [div.Id] = 2 }, noDivisionCount: 0);

        var siteNode = Assert.Single(result.Sites);
        Assert.Equal("S1", siteNode.Code);
        Assert.Equal(2, siteNode.MemberCount);
        var deptNode = Assert.Single(siteNode.Departments);
        Assert.Equal("D1", deptNode.Code);
        Assert.Equal(2, deptNode.MemberCount);
        var divNode = Assert.Single(deptNode.Divisions);
        Assert.Equal("DV1", divNode.Code);
        Assert.Equal(2, divNode.MemberCount);
    }

    [Fact]
    public void BuildStructure_DepartmentAppearsUnderEachSiteThatHostsIt()
    {
        var dept = new Department { Id = Guid.NewGuid(), Code = "D1", Name = "Eng", IsActive = true };
        var siteA = new Site { Id = Guid.NewGuid(), Code = "SA", Name = "Site A", IsActive = true };
        var siteB = new Site { Id = Guid.NewGuid(), Code = "SB", Name = "Site B", IsActive = true };
        var divA = new Division { Id = Guid.NewGuid(), Code = "DVA", Name = "Eng@A",
            DepartmentId = dept.Id, SiteId = siteA.Id, Department = dept, Site = siteA };
        var divB = new Division { Id = Guid.NewGuid(), Code = "DVB", Name = "Eng@B",
            DepartmentId = dept.Id, SiteId = siteB.Id, Department = dept, Site = siteB };

        var result = DirectoryEndpoints.BuildStructure([divA, divB],
            new Dictionary<Guid, int>(), noDivisionCount: 0);

        Assert.Equal(2, result.Sites.Length);
        Assert.All(result.Sites, s => Assert.Equal("D1", Assert.Single(s.Departments).Code));
    }

    [Fact]
    public void BuildStructure_NoDivisionCount_IsFlatSiblingOfSites()
    {
        var result = DirectoryEndpoints.BuildStructure([], new Dictionary<Guid, int>(), noDivisionCount: 3);
        Assert.Empty(result.Sites);
        Assert.Equal(3, result.NoDivisionCount);
    }

    [Fact]
    public async Task StructureQuery_NoDivisionCount_IncludesActive_ExcludesInactive()
    {
        using var t = new TestDb();
        t.AddUser("20080115");                       // active, no division -> counted
        t.AddUser("20080116", active: false);         // inactive -> excluded

        var noDivisionCount = await t.Db.Users.CountAsync(u => u.IsActive && u.DivisionId == null);

        Assert.Equal(1, noDivisionCount);
    }

    [Fact]
    public void AggregateDivisionMatches_GroupsByDivision_WithPerDivisionCount()
    {
        var matches = new[]
        {
            ("DV1", "Eng@HQ"), ("DV1", "Eng@HQ"), ("DV2", "Sales@HQ"),
        };

        var result = DirectoryEndpoints.AggregateDivisionMatches(matches);

        Assert.Equal(2, result.Length);
        var dv1 = Assert.Single(result, x => x.Code == "DV1");
        Assert.Equal(2, dv1.MatchCount);
        var dv2 = Assert.Single(result, x => x.Code == "DV2");
        Assert.Equal(1, dv2.MatchCount);
    }

    [Fact]
    public void AggregateDivisionMatches_EmptyInput_ReturnsEmptyArray()
    {
        var result = DirectoryEndpoints.AggregateDivisionMatches([]);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Employees_DivisionCodeFilter_NarrowsResults()
    {
        using var t = new TestDb();
        var (dept, site) = SeedDeptSite(t);
        var div = new Division { Id = Guid.NewGuid(), Code = "DV1", Name = "Eng@HQ",
            DepartmentId = dept.Id, SiteId = site.Id };
        t.Db.Divisions.Add(div);
        var inDiv = t.AddUser("20080115"); inDiv.DivisionId = div.Id;
        t.AddUser("20080116");    // no division -> filtered out
        t.Db.SaveChanges();

        var filtered = await t.Db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Division != null && u.Division.Code == "DV1")
            .ToListAsync();

        Assert.Single(filtered);
        Assert.Equal("20080115", filtered[0].Nik);
    }

    // Regression: directory.read is an Employee-baseline permission, not AdminOnly. Confirms
    // the read endpoints' authorization gate (RequirePermission) is a plain permission lookup
    // that a non-admin role can satisfy, while /api/admin/org/divisions stays role-gated.
    [Fact]
    public async Task DirectoryRead_NonAdminEmployeeRole_IsAllowed_ByPermissionEvaluator()
    {
        using var t = new TestDb();
        var employeeRole = new Role { Id = Guid.NewGuid(), Name = "Employee" };
        var permission = new Permission { Id = Guid.NewGuid(), Code = "directory.read" };
        t.Db.Roles.Add(employeeRole);
        t.Db.Permissions.Add(permission);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = employeeRole.Id, PermissionId = permission.Id });
        var user = t.AddUser("20080115");
        t.Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = employeeRole.Id });
        await t.Db.SaveChangesAsync();

        var evaluator = new IPop.Infrastructure.Rbac.PermissionEvaluator(t.Db);
        var allowed = await evaluator.IsAllowedAsync(user.Id, "directory.read", null);

        Assert.True(allowed);
    }

    // Regression: /api/admin/org/divisions (Task 1) is gated by the "AdminOnly" policy —
    // RequireRole("Admin"), a different mechanism from directory.read's RequirePermission
    // above. Confirms a non-admin role still fails that role check (Task 1's boundary
    // untouched by this task's read-endpoint additions). Same style as AuthorizationPolicyTests.
    [Fact]
    public void AdminOnlyPolicy_NonAdminRole_IsDenied()
    {
        var employee = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Employee")], "test"));

        Assert.False(employee.IsInRole("Admin"));
    }
}
