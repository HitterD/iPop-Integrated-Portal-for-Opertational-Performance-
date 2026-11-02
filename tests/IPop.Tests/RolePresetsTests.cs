using IPop.Core.Entities;
using IPop.Infrastructure.Rbac;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Permission presets: ready-made roles for HR (news) and HC (approval / directory).
public class RolePresetsTests
{
    private static void SeedPermissions(TestDb t)
    {
        foreach (var c in new[] { "news.read", "news.write", "news.publish", "approval.read", "approval.manage", "directory.manage", "chat.admin" })
            t.Db.Permissions.Add(new Permission { Id = Guid.NewGuid(), Code = c, Description = c });
        t.Db.SaveChanges();
    }

    private static HashSet<string> Grants(IPop.Infrastructure.Persistence.IPopDbContext db, string role) =>
        db.RolePermissions.Where(rp => rp.Role.Name == role).Select(rp => rp.Permission.Code).ToHashSet();

    [Fact]
    public async Task Ensure_CreatesPresetsWithExactGrants()
    {
        using var t = new TestDb(); SeedPermissions(t);
        await RolePresets.EnsureAsync(t.Db, default);
        var db = t.Verify();
        Assert.Equal(new HashSet<string> { "news.read", "news.write", "news.publish" }, Grants(db, RolePresets.HrNews));
        Assert.Equal(new HashSet<string> { "approval.read", "approval.manage" }, Grants(db, RolePresets.HcApproval));
        Assert.Equal(new HashSet<string> { "approval.read", "approval.manage", "directory.manage" }, Grants(db, RolePresets.HcApprovalDirectory));
        Assert.Equal(new HashSet<string> { "approval.read", "approval.manage", "directory.manage", "news.read", "news.write", "news.publish" },
            Grants(db, RolePresets.HcFull));
        Assert.Equal("HC – Lengkap", RolePresets.HcFull);
    }

    [Fact]
    public async Task Ensure_KeepsExistingHrRoleAndMembers()
    {
        using var t = new TestDb(); SeedPermissions(t);
        var hr = new Role { Id = Guid.NewGuid(), Name = "HR" };
        t.Db.Roles.Add(hr);
        var u = t.AddUser("E1");
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = hr.Id });
        t.Db.SaveChanges();

        await RolePresets.EnsureAsync(t.Db, default);

        var db = t.Verify();
        Assert.Single(db.Roles.Where(r => r.Name == "HR"));
        Assert.True(db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == hr.Id));
        Assert.Equal("HR – News", RolePresets.Label("HR"));
    }

    [Fact]
    public async Task Ensure_IsIdempotentAndKeepsExtraGrants()
    {
        using var t = new TestDb(); SeedPermissions(t);
        await RolePresets.EnsureAsync(t.Db, default);
        var role = t.Db.Roles.Single(r => r.Name == RolePresets.HcApproval);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = t.Db.Permissions.Single(p => p.Code == "chat.admin").Id });
        t.Db.SaveChanges();

        await RolePresets.EnsureAsync(t.Db, default);
        await RolePresets.EnsureAsync(t.Db, default);

        var db = t.Verify();
        Assert.Equal(4, db.Roles.Count());
        Assert.Contains("chat.admin", Grants(db, RolePresets.HcApproval));
        Assert.Equal(3, Grants(db, RolePresets.HcApproval).Count);
    }

    [Fact]
    public async Task AllowedCodes_UnionsRolesAndOverrides()
    {
        using var t = new TestDb(); SeedPermissions(t);
        await RolePresets.EnsureAsync(t.Db, default);
        var u = t.AddUser("E1");
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = t.Db.Roles.Single(r => r.Name == RolePresets.HcApproval).Id });
        t.Db.UserPermissionOverrides.Add(new UserPermissionOverride { UserId = u.Id,
            PermissionId = t.Db.Permissions.Single(p => p.Code == "chat.admin").Id, Effect = PermissionEffect.Allow, DepartmentCode = "" });
        t.Db.UserPermissionOverrides.Add(new UserPermissionOverride { UserId = u.Id,
            PermissionId = t.Db.Permissions.Single(p => p.Code == "approval.manage").Id, Effect = PermissionEffect.Deny, DepartmentCode = "" });
        t.Db.SaveChanges();

        var codes = await new PermissionEvaluator(t.Verify()).AllowedCodesAsync(u.Id, default);

        Assert.Contains("approval.read", codes);
        Assert.Contains("chat.admin", codes);
        Assert.DoesNotContain("approval.manage", codes);
        Assert.DoesNotContain("directory.manage", codes);
    }
}
