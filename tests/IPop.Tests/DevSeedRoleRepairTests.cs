using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Pentest V-01 data repair: the shared DB had Employee.ParentRoleId = Admin.
public class DevSeedRoleRepairTests
{
    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string hash, string password) => hash == "h:" + password;
    }

    [Fact]
    public async Task Seed_unlinks_roles_from_Admin_and_keeps_employee_project_rights_once()
    {
        using var t = new TestDb();
        await DevSeed.EnsureSeededAsync(t.Db, new FakeHasher());
        var admin = await t.Db.Roles.SingleAsync(r => r.Name == "Admin");
        var employee = await t.Db.Roles.SingleAsync(r => r.Name == "Employee");
        var projectPerms = await t.Db.Permissions.Where(p => p.Code == "projects.write" || p.Code == "projects.admin")
            .Select(p => p.Id).ToListAsync();

        // Recreate the broken state: Employee under Admin, without its own project grants.
        employee.ParentRoleId = admin.Id;
        t.Db.RolePermissions.RemoveRange(t.Db.RolePermissions.Where(rp => rp.RoleId == employee.Id && projectPerms.Contains(rp.PermissionId)));
        await t.Db.SaveChangesAsync();

        await DevSeed.EnsureSeededAsync(t.Verify(), new FakeHasher());
        var db = t.Verify();
        Assert.Null((await db.Roles.SingleAsync(r => r.Id == employee.Id)).ParentRoleId);
        Assert.Equal(2, await db.RolePermissions.CountAsync(rp => rp.RoleId == employee.Id && projectPerms.Contains(rp.PermissionId)));

        // An admin later revokes them: a normal restart must not grant them back.
        db.RolePermissions.RemoveRange(db.RolePermissions.Where(rp => rp.RoleId == employee.Id && projectPerms.Contains(rp.PermissionId)));
        await db.SaveChangesAsync();
        await DevSeed.EnsureSeededAsync(t.Verify(), new FakeHasher());
        Assert.Equal(0, await t.Verify().RolePermissions.CountAsync(rp => rp.RoleId == employee.Id && projectPerms.Contains(rp.PermissionId)));
    }
}
