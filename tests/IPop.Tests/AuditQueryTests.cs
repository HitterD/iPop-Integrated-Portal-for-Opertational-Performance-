using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class AuditQueryTests
{
    [Fact]
    public async Task BuildAuditQuery_FiltersByType()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        t.Db.AuditLogs.AddRange(
            new AuditLog { Action = "admin.session.revoke", Entity = "AuthSession", CreatedAt = now },
            new AuditLog { Action = "auth.login", Entity = "User", CreatedAt = now },
            new AuditLog { Action = "admin.user.create", Entity = "User", CreatedAt = now },
            new AuditLog { Action = "hris.sync", Entity = "HrisSyncState", CreatedAt = now },
            new AuditLog { Action = "admin.org.dept.create", Entity = "Department", CreatedAt = now },
            new AuditLog { Action = "settings.changed", Entity = "AppSetting", CreatedAt = now }
        );
        await t.Db.SaveChangesAsync();

        var sec = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, "security", null, null).ToListAsync();
        Assert.Equal(2, sec.Count);
        Assert.All(sec, x => Assert.True(x.Action.StartsWith("admin.session") || x.Action.StartsWith("auth")));

        var usr = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, "user", null, null).ToListAsync();
        Assert.Single(usr);
        Assert.Equal("admin.user.create", usr[0].Action);

        var sync = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, "sync", null, null).ToListAsync();
        Assert.Single(sync);
        Assert.Equal("hris.sync", sync[0].Action);

        var org = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, "org", null, null).ToListAsync();
        Assert.Single(org);
        Assert.Equal("admin.org.dept.create", org[0].Action);

        var set = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, "settings", null, null).ToListAsync();
        Assert.Single(set);
        Assert.Equal("settings.changed", set[0].Action);
    }

    [Fact]
    public async Task BuildAuditQuery_FiltersBySearchQuery()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        t.Db.AuditLogs.AddRange(
            new AuditLog { ActorNik = "20140312", Action = "admin.user.edit", Entity = "User", EntityId = "00012345", CreatedAt = now },
            new AuditLog { ActorNik = "20080115", Action = "admin.session.revoke", Entity = "AuthSession", EntityId = "sess-1", CreatedAt = now }
        );
        await t.Db.SaveChangesAsync();

        var res = await AuditEndpoints.BuildAuditQuery(t.Db, "00012345", null, null, null, null, null, null).ToListAsync();
        Assert.Single(res);
        Assert.Equal("20140312", res[0].ActorNik);

        var resActor = await AuditEndpoints.BuildAuditQuery(t.Db, "20080115", null, null, null, null, null, null).ToListAsync();
        Assert.Single(resActor);
        Assert.Equal("admin.session.revoke", resActor[0].Action);
    }
}
