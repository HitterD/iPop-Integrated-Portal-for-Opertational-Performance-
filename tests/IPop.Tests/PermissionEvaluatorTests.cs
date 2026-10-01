using IPop.Core.Entities;
using IPop.Infrastructure.Rbac;
using Xunit;

namespace IPop.Tests;

/// Probes the role-inheritance walk in PermissionEvaluator. Roles carry ParentRoleId
/// (PRD §2 dept → sub-dept inheritance) and the evaluator walks parents level by level.
public class PermissionEvaluatorTests
{
    private static (Role role, Permission perm) Seed(TestDb t, string roleName, string code)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = roleName };
        var perm = new Permission { Id = Guid.NewGuid(), Code = code, Description = code };
        t.Db.Roles.Add(role);
        t.Db.Permissions.Add(perm);
        t.Db.SaveChanges();
        return (role, perm);
    }

    [Fact]
    public async Task Direct_role_grant_is_allowed()
    {
        using var t = new TestDb();
        var u = t.AddUser("D1");
        var (role, perm) = Seed(t, "R", "thing.read");
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = role.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    /// Pentest V-01: Employee had ParentRoleId = Admin, so every user inherited news.publish,
    /// chat.admin, projects.admin… Admin's grants must only reach direct members.
    [Fact]
    public async Task Admin_grants_are_not_inherited_by_child_roles()
    {
        using var t = new TestDb();
        var u = t.AddUser("V01");
        var (admin, perm) = Seed(t, "Admin", "news.publish");
        var employee = new Role { Id = Guid.NewGuid(), Name = "Employee", ParentRoleId = admin.Id };
        t.Db.Roles.Add(employee);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        // Role-less user → default Employee role, and an explicit Employee member: both denied.
        var sut = new PermissionEvaluator(t.Verify());
        Assert.False(await sut.IsAllowedAsync(u.Id, "news.publish", null));
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = employee.Id });
        t.Db.SaveChanges();
        Assert.False(await new PermissionEvaluator(t.Verify()).IsAllowedAsync(u.Id, "news.publish", null));

        // A direct Admin member still gets it.
        var a = t.AddUser("V01A");
        t.Db.UserRoles.Add(new UserRole { UserId = a.Id, RoleId = admin.Id });
        t.Db.SaveChanges();
        Assert.True(await new PermissionEvaluator(t.Verify()).IsAllowedAsync(a.Id, "news.publish", null));
    }

    [Fact]
    public async Task Permission_inherited_from_parent_role_is_allowed()
    {
        using var t = new TestDb();
        var u = t.AddUser("D2");
        var (parent, perm) = Seed(t, "Parent", "thing.read");
        var child = new Role { Id = Guid.NewGuid(), Name = "Child", ParentRoleId = parent.Id };
        t.Db.Roles.Add(child);
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = child.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = parent.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    /// Two roles, one of which is a root (no parent). The grant lives on the OTHER role's parent.
    /// The evaluator must keep walking; a root role in the batch must not end the walk.
    [Fact]
    public async Task Root_sibling_role_does_not_abort_the_parent_walk()
    {
        using var t = new TestDb();
        var u = t.AddUser("D3");
        var (grandparent, perm) = Seed(t, "Grandparent", "thing.read");
        var child = new Role { Id = Guid.NewGuid(), Name = "Child", ParentRoleId = grandparent.Id };
        var rootSibling = new Role { Id = Guid.NewGuid(), Name = "RootSibling" };
        t.Db.Roles.AddRange(child, rootSibling);
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = child.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = rootSibling.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = grandparent.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    /// A diamond: two roles that share the same parent. The shared parent is visited twice in
    /// one batch. That is legal inheritance, not a cycle, and must not deny.
    [Fact]
    public async Task Diamond_inheritance_shared_parent_is_allowed()
    {
        using var t = new TestDb();
        var u = t.AddUser("D4");
        var (shared, perm) = Seed(t, "Shared", "thing.read");
        var a = new Role { Id = Guid.NewGuid(), Name = "A", ParentRoleId = shared.Id };
        var b = new Role { Id = Guid.NewGuid(), Name = "B", ParentRoleId = shared.Id };
        t.Db.Roles.AddRange(a, b);
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = a.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = b.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = shared.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    [Fact]
    public async Task User_deny_override_beats_role_grant()
    {
        using var t = new TestDb();
        var u = t.AddUser("D5");
        var (role, perm) = Seed(t, "R", "thing.read");
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = role.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        { UserId = u.Id, PermissionId = perm.Id, Effect = PermissionEffect.Deny, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.False(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    [Fact]
    public async Task Unknown_permission_code_is_denied()
    {
        using var t = new TestDb();
        var u = t.AddUser("D6");
        var sut = new PermissionEvaluator(t.Verify());
        Assert.False(await sut.IsAllowedAsync(u.Id, "no.such.permission", null));
    }

    /// The user directly holds BOTH a child role and one of its ancestors.
    /// Child -> Mid -> Top, user has [Child, Mid], and only Top grants the permission.
    /// Level 1 visits {Child, Mid}; level 2's batch re-encounters Mid, so the walk must
    /// SKIP it and carry on to Top rather than abandoning the whole evaluation.
    [Fact]
    public async Task Ancestor_held_directly_does_not_abort_the_walk_before_its_own_parent()
    {
        using var t = new TestDb();
        var u = t.AddUser("D7");
        var (top, perm) = Seed(t, "Top", "thing.read");
        var mid = new Role { Id = Guid.NewGuid(), Name = "Mid", ParentRoleId = top.Id };
        t.Db.Roles.Add(mid);
        t.Db.SaveChanges();
        var child = new Role { Id = Guid.NewGuid(), Name = "Child", ParentRoleId = mid.Id };
        t.Db.Roles.Add(child);
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = child.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = mid.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = top.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    /// Diamond where the grant sits ABOVE the shared parent. Level 2's batch contains the
    /// shared parent twice; the second occurrence must be skipped, not treated as a cycle.
    [Fact]
    public async Task Diamond_with_grant_above_shared_parent_is_allowed()
    {
        using var t = new TestDb();
        var u = t.AddUser("D8");
        var (top, perm) = Seed(t, "Top", "thing.read");
        var shared = new Role { Id = Guid.NewGuid(), Name = "Shared", ParentRoleId = top.Id };
        t.Db.Roles.Add(shared);
        t.Db.SaveChanges();
        var a = new Role { Id = Guid.NewGuid(), Name = "A", ParentRoleId = shared.Id };
        var b = new Role { Id = Guid.NewGuid(), Name = "B", ParentRoleId = shared.Id };
        t.Db.Roles.AddRange(a, b);
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = a.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = b.Id });
        t.Db.RolePermissions.Add(new RolePermission { RoleId = top.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "thing.read", null));
    }

    /// The `visited` set exists to stop an infinite walk on a cyclic hierarchy. RbacEndpoints
    /// rejects cycles at write time (CreatesCycle), but a cycle already in the database — or
    /// one written by a migration/seed that bypasses the endpoint — must still terminate.
    [Fact]
    public async Task Cyclic_hierarchy_terminates_and_denies()
    {
        using var t = new TestDb();
        var u = t.AddUser("D9");
        var (a, perm) = Seed(t, "A", "thing.read");
        var b = new Role { Id = Guid.NewGuid(), Name = "B", ParentRoleId = a.Id };
        t.Db.Roles.Add(b);
        t.Db.SaveChanges();
        a.ParentRoleId = b.Id;              // close the loop: A -> B -> A
        t.Db.SaveChanges();
        t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = a.Id });
        t.Db.SaveChanges();                 // nobody grants `perm`, so the walk must exhaust

        var sut = new PermissionEvaluator(t.Verify());
        var task = sut.IsAllowedAsync(u.Id, "thing.read", null);
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, done);            // fails by timeout if the walk never terminates
        Assert.False(await task);
    }

    [Fact]
    public async Task User_without_explicit_roles_inherits_Employee_default_role()
    {
        using var t = new TestDb();
        var u = t.AddUser("D10"); // No UserRoles assigned
        var (empRole, perm) = Seed(t, "Employee", "calls.use");
        t.Db.RolePermissions.Add(new RolePermission { RoleId = empRole.Id, PermissionId = perm.Id, DepartmentCode = "" });
        t.Db.SaveChanges();

        var sut = new PermissionEvaluator(t.Verify());
        Assert.True(await sut.IsAllowedAsync(u.Id, "calls.use", null));
    }
}
