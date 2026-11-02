using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Approval;
using IPop.Infrastructure.Rbac;
using Xunit;

namespace IPop.Tests;

/// /api/approval: own chain without NIKs, slot validation, import token + directory permission, and
/// the /api/admin/org group opened to directory.manage.
public class ApprovalEndpointsTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    private static async Task<User> UserWith(TestDb t, string nik, params string[] codes)
    {
        foreach (var c in codes.Where(c => !t.Db.Permissions.Any(p => p.Code == c)))
            t.Db.Permissions.Add(new Permission { Id = Guid.NewGuid(), Code = c, Description = c });
        await t.Db.SaveChangesAsync();
        var u = t.AddUser(nik);
        var role = new Role { Id = Guid.NewGuid(), Name = "R-" + nik };
        t.Db.Roles.Add(role);
        foreach (var c in codes)
            t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = t.Db.Permissions.Single(p => p.Code == c).Id });
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = role.Id });
        await t.Db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Me_ReturnsOwnChainWithoutNiks()
    {
        using var t = new TestDb();
        t.AddUser("E1"); var boss = t.AddUser("BOSS"); boss.Name = "Nico Budiman"; t.Db.SaveChanges();
        var c = new ApproverChain { Nik = "E1", UpdatedAt = Now }; c.SetSlot(1, "BOSS");
        t.Db.ApproverChains.Add(c); t.Db.SaveChanges();

        var steps = await new ApprovalService(t.Db, () => Now).MineAsync("E1", default);

        var step = Assert.Single(steps);
        Assert.Equal("Manager", step.Label);
        Assert.Equal("Nico Budiman", step.Name);
        Assert.DoesNotContain("BOSS", JsonSerializer.Serialize(steps));
    }

    [Fact]
    public async Task Put_Requires9Slots()
    {
        using var t = new TestDb();
        t.AddUser("E1");
        var r = await ApprovalEndpoints.PutChainAsync(new ApprovalService(t.Db, () => Now), "E1",
            new ApprovalEndpoints.SetSlotsRequest(["A", "B"]), "HC1", default);
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public async Task Apply_WithStaleToken_Returns409()
    {
        using var t = new TestDb();
        await UserWith(t, "HC1", "approval.manage", "directory.manage");
        var bytes = "file-v1"u8.ToArray();
        var r = await ApprovalEndpoints.ApplyCoreAsync(t.Db, MakeImport(t), new WorkbookParseResult([], 0, []), bytes, false,
            token: "not-the-token", "HC1", canDirectory: true, default);
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public async Task Apply_TreeChangesWithoutDirectoryManage_Returns403()
    {
        using var t = new TestDb();
        var sep = new Site { Id = Guid.NewGuid(), Code = "SJA 1", Name = "SJA SEPANJANG" };
        t.Db.Sites.Add(sep);
        t.Db.Departments.Add(new Department { Id = Guid.NewGuid(), Code = "OPS", Name = "Operational", SiteId = sep.Id });
        t.AddUser("E1");
        t.Db.SaveChanges();
        var parsed = new WorkbookParseResult([new WorkbookRow(2, "E1", "E1", "OPERATION SJA 1", "PRODUCTION 1", null, "PACKING AB", null, null, "SJA-1", new string?[9])], 0, []);
        var bytes = "file-v1"u8.ToArray();
        var token = ApprovalEndpoints.TokenOf(bytes, false);

        var r = await ApprovalEndpoints.ApplyCoreAsync(t.Db, MakeImport(t), parsed, bytes, false, token, "HC1", canDirectory: false, default);

        Assert.Equal(403, r.Status);
        Assert.Contains("Directory", r.Message);
        Assert.Empty(t.Verify().ApproverChains);
    }

    [Fact]
    public async Task Apply_WithMatchingToken_Writes()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("M");
        var parsed = new WorkbookParseResult([new WorkbookRow(2, "E1", "E1", null, null, null, null, null, null, null, ["M", null, null, null, null, null, null, null, null])], 0, []);
        var bytes = "file-v1"u8.ToArray();

        var r = await ApprovalEndpoints.ApplyCoreAsync(t.Db, MakeImport(t), parsed, bytes, false, ApprovalEndpoints.TokenOf(bytes, false), "HC1", canDirectory: false, default);

        Assert.Equal(200, r.Status);
        Assert.Equal("M", t.Verify().Users.Single(u => u.Nik == "E1").ManagerNik);
    }

    [Fact]
    public void Token_DependsOnBytesAndCompleteFlag()
    {
        var a = ApprovalEndpoints.TokenOf("x"u8.ToArray(), false);
        Assert.Equal(a, ApprovalEndpoints.TokenOf("x"u8.ToArray(), false));
        Assert.NotEqual(a, ApprovalEndpoints.TokenOf("x"u8.ToArray(), true));
        Assert.NotEqual(a, ApprovalEndpoints.TokenOf("y"u8.ToArray(), false));
    }

    [Fact]
    public async Task OrgGroup_AllowsDirectoryManageNonAdmin_DeniesPlainEmployee()
    {
        using var t = new TestDb();
        var hc = await UserWith(t, "HC1", "directory.manage");
        var emp = await UserWith(t, "EMP", "directory.read");
        var eval = new PermissionEvaluator(t.Verify());
        Assert.True(await eval.IsAllowedAsync(hc.Id, ApprovalEndpoints.OrgPermission, null));
        Assert.False(await eval.IsAllowedAsync(emp.Id, ApprovalEndpoints.OrgPermission, null));
    }

    private static ApprovalImport MakeImport(TestDb t) => new(t.Db, new ApprovalService(t.Db, () => Now), new TreePlanner(t.Db));
}
