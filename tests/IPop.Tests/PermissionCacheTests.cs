using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Rbac;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class PermissionCacheTests
{
    [Fact]
    public async Task Cached_answer_is_dropped_the_moment_an_admin_changes_a_role()
    {
        using var t = new TestDb();
        var u = t.AddUser("PC1");
        var role = t.EnsureRole("Employee");
        t.AssignRole(u.Id, "Employee");
        var perm = new Permission { Id = Guid.NewGuid(), Code = "mail.read", Description = "x" };
        t.Db.Permissions.Add(perm);
        t.Db.SaveChanges();

        var cache = new PermissionCache();
        var interceptor = new RbacChangeInterceptor(cache);
        var options = new DbContextOptionsBuilder<IPopDbContext>()
            .UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(interceptor).Options;

        await using (var db = new IPopDbContext(options))
            Assert.False(await new PermissionEvaluator(db, cache).IsAllowedAsync(u.Id, "mail.read", null));

        // Admin grants the permission through EF (as RbacEndpoints does).
        await using (var db = new IPopDbContext(options))
        {
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
            await db.SaveChangesAsync();
        }

        await using (var db = new IPopDbContext(options))
            Assert.True(await new PermissionEvaluator(db, cache).IsAllowedAsync(u.Id, "mail.read", null));
    }

    [Fact]
    public async Task Repeated_checks_are_served_from_the_cache()
    {
        using var t = new TestDb();
        var u = t.AddUser("PC2");
        var cache = new PermissionCache();
        cache.Set(u.Id, "chat.read", null, true, DateTime.UtcNow);

        // No permission rows exist, so only the cache can say yes.
        Assert.True(await new PermissionEvaluator(t.Verify(), cache).IsAllowedAsync(u.Id, "chat.read", null));
        Assert.False(await new PermissionEvaluator(t.Verify(), cache).IsAllowedAsync(u.Id, "chat.write", null));
    }
}
