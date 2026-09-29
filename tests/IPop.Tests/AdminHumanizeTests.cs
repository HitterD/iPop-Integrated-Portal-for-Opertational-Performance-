using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Web.Components.Admin;
using IPop.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class AdminHumanizeTests
{
    private static AuditRowDto Row(string action, string? actor = "001", string? entityId = null, DateTime? at = null,
        string? actorName = null, string? entityName = null, string? entity = "User") =>
        new(Guid.NewGuid(), actor, action, entity, entityId, null, null, at ?? DateTime.UtcNow, actorName, entityName);

    [Theory]
    [InlineData("\"Chromium\";v=\"128\", \"Google Chrome\";v=\"128\"|Windows|desktop|en-US|1920x1080", "Chrome on Windows", "laptop", false)]
    [InlineData("\"Microsoft Edge\";v=\"128\", \"Chromium\";v=\"128\"|macOS|desktop|en|1440x900", "Edge on macOS", "laptop", false)]
    [InlineData("\"Google Chrome\";v=\"128\"|Android|mobile|id-ID|390x844", "Chrome on Android", "smartphone", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 lik|iPhone|mobile|en|390x844", "Browser on iPhone", "smartphone", false)]
    [InlineData("\"HeadlessChrome\";v=\"128\"|Windows|desktop|en-US|1440x900", "Headless Chrome on Windows", "bot", true)]
    [InlineData(null, "Unknown device", "monitor", false)]
    public void ParseDevice_NamesBrowserAndOs(string? fp, string label, string icon, bool automated)
    {
        var d = AdminHumanize.ParseDevice(fp);
        Assert.Equal(label, d.Label);
        Assert.Equal(icon, d.Icon);
        Assert.Equal(automated, d.Automated);
    }

    [Fact]
    public void Sentence_UsesNamesAndFriendlyUnits()
    {
        Assert.Equal("Ana signed in", AdminHumanize.Sentence(Row("auth.login", actorName: "Ana")));
        Assert.Equal("Ana locked Budi's account",
            AdminHumanize.Sentence(Row("admin.user.lock", entityId: "002", actorName: "Ana", entityName: "Budi")));
        Assert.Equal("Ana changed their own role",
            AdminHumanize.Sentence(Row("admin.user.roles", entityId: "001", actorName: "Ana")));
        Assert.Equal("Ana added department QA",
            AdminHumanize.Sentence(Row("admin.org.division.create", entityId: "QA", actorName: "Ana", entity: "Division")));
        Assert.Equal("Ana signed out 3 automated sessions",
            AdminHumanize.Sentence(Row("admin.session.revoke-automated", entityId: "revoked=3", actorName: "Ana")));
        Assert.Equal("HRIS sync ran automatically", AdminHumanize.Sentence(Row("hris.sync", actor: null)));
        Assert.Equal("System · something.new", AdminHumanize.Sentence(Row("something.new", actor: "system")));
    }

    [Fact]
    public void Collapse_MergesConsecutiveSameEventsOnly()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            Row("auth.login", at: now),
            Row("auth.login", at: now.AddSeconds(-10)),
            Row("auth.login", at: now.AddSeconds(-20)),
            Row("admin.user.edit", entityId: "002", at: now.AddSeconds(-30)),
            Row("auth.login", at: now.AddSeconds(-40)),
            Row("auth.login", actor: "009", at: now.AddSeconds(-50)),
        };
        var groups = AdminHumanize.Collapse(rows);
        Assert.Equal([3, 1, 1, 1], groups.Select(g => g.Count));
        Assert.Equal(now.AddSeconds(-20), groups[0].Oldest);
    }

    [Fact]
    public async Task BuildAuditQuery_UserFilter_MatchesActorOrTarget()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        t.Db.AuditLogs.AddRange(
            new AuditLog { ActorNik = "001", Action = "auth.login", Entity = "User", EntityId = "001", CreatedAt = now },
            new AuditLog { ActorNik = "900", Action = "admin.user.lock", Entity = "User", EntityId = "001", CreatedAt = now },
            new AuditLog { ActorNik = "900", Action = "admin.org.site.edit", Entity = "Site", EntityId = "001", CreatedAt = now },
            new AuditLog { ActorNik = "002", Action = "auth.login", Entity = "User", EntityId = "002", CreatedAt = now });
        await t.Db.SaveChangesAsync();

        var res = await AuditEndpoints.BuildAuditQuery(t.Db, null, null, null, null, null, null, null, "001").ToListAsync();
        Assert.Equal(2, res.Count);
        Assert.DoesNotContain(res, x => x.Entity == "Site");
    }

    [Fact]
    public async Task WithNames_ResolvesActorAndUserTargets()
    {
        using var t = new TestDb();
        t.AddUser("001");
        t.AddUser("002");
        var now = DateTime.UtcNow;
        var rows = new List<AuditEndpoints.AuditRow>
        {
            new(Guid.NewGuid(), "001", "admin.user.lock", "User", "002", null, null, now),
            new(Guid.NewGuid(), "001", "admin.org.site.edit", "Site", "002", null, null, now),
            new(Guid.NewGuid(), "system", "hris.sync", null, null, null, null, now),
        };

        var named = await AuditEndpoints.WithNames(t.Db, rows, default);
        Assert.Equal("User 001", named[0].ActorName);
        Assert.Equal("User 002", named[0].EntityName);
        Assert.Null(named[1].EntityName);
        Assert.Null(named[2].ActorName);
    }

    [Theory]
    [InlineData("'IZZUDDIN 'ULWAN", "IU")]
    [InlineData("A. MALIK MUSLIK", "AM")]
    [InlineData("333333", "33")]
    [InlineData("...", "?")]
    public void Initials_SkipPunctuation(string name, string expected) =>
        Assert.Equal(expected, AdminOrgModel.Initials(name));
}
