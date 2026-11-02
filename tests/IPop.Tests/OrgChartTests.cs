using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using P = IPop.Api.Endpoints.OrgChartEndpoints.PublishPositionDto;
using Req = IPop.Api.Endpoints.OrgChartEndpoints.PublishChartRequest;

namespace IPop.Tests;

/// Org chart Publish: full replace, shape validation, version conflicts, and Position written (ManagerNik
/// belongs to the approver matrix and is never touched by Publish)
/// back to users. Same seam as DivisionAdminTests (TestDb + the production helper).
public class OrgChartTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Task<(int Status, object? Error, int Version)> Publish(TestDb t, int version, params P[] ps) =>
        OrgChartEndpoints.PublishAsync(t.Db, new Req(version, [.. ps]), "ADMIN", Now, CancellationToken.None);

    [Fact]
    public async Task Publish_WritesPositionsHoldersAndUserFields()
    {
        using var t = new TestDb();
        t.AddUser("100"); t.AddUser("200"); t.AddUser("300"); t.AddUser("400");
        t.Db.Departments.Add(new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT" });
        t.Db.SaveChanges();

        var r = await Publish(t, 0,
            new P("a", "President Director", null, null, null, 1, ["100"]),
            new P("b", "ICT Manager", "a", "ICT", null, 1, []),
            new P("c", "Developer", "b", "ICT", null, 1, ["200", "300"]));

        Assert.Equal(200, r.Status);
        Assert.Equal(1, r.Version);
        var db = t.Verify();
        Assert.Equal(3, db.OrgPositions.Count());
        Assert.Equal(3, db.OrgPositionHolders.Count());
        var dev = db.Users.Single(u => u.Nik == "200");
        Assert.Equal("Developer", dev.Position);
        // ManagerNik comes from the approver matrix now; Publish leaves it alone.
        Assert.Null(dev.ManagerNik);
        var top = db.Users.Single(u => u.Nik == "100");
        Assert.Equal("President Director", top.Position);
        Assert.Null(top.ManagerNik);
        Assert.Equal("ICT", db.OrgPositions.Include(p => p.Department).Single(p => p.Title == "ICT Manager").Department!.Code);
        Assert.Single(db.AuditLogs.Where(a => a.Action == "admin.org.chart.publish"));
    }

    [Fact]
    public async Task Publish_ClearsPeopleNoLongerOnTheChart_ButEmptyChartWipesNobody()
    {
        using var t = new TestDb();
        var off = t.AddUser("900");
        off.Position = "Old HRIS title"; off.ManagerNik = "1";
        t.AddUser("100");
        t.Db.SaveChanges();

        Assert.Equal(200, (await Publish(t, 0)).Status);
        Assert.Equal("Old HRIS title", t.Verify().Users.Single(u => u.Nik == "900").Position);

        Assert.Equal(200, (await Publish(t, 1, new P("a", "Director", null, null, null, 1, ["100"]))).Status);
        var u = t.Verify().Users.Single(x => x.Nik == "900");
        Assert.Null(u.Position);
        Assert.Equal("1", u.ManagerNik);
    }

    [Fact]
    public async Task Publish_ConcurrentRoles_UsesHighestBoxAndSkipsSelfAsManager()
    {
        using var t = new TestDb();
        t.AddUser("100"); t.AddUser("200");
        var r = await Publish(t, 0,
            new P("a", "Director", null, null, null, 1, ["100"]),
            new P("b", "Plant Manager", "a", null, null, 1, ["100"]),
            new P("c", "Supervisor", "b", null, null, 1, ["200"]));
        Assert.Equal(200, r.Status);
        var db = t.Verify();
        Assert.Equal("Director", db.Users.Single(u => u.Nik == "100").Position);
        Assert.Null(db.Users.Single(u => u.Nik == "200").ManagerNik);
        Assert.Equal(2, db.OrgPositionHolders.Count(h => h.User.Nik == "100"));
    }

    [Fact]
    public async Task Publish_ReplacesPreviousChart_AndKeepsExistingIds()
    {
        using var t = new TestDb();
        t.AddUser("100");
        await Publish(t, 0, new P("a", "Director", null, null, null, 1, ["100"]), new P("b", "Old box", "a", null, null, 1, []));
        var dirId = t.Verify().OrgPositions.Single(p => p.Title == "Director").Id.ToString();

        var r = await Publish(t, 1, new P(dirId, "Director", null, null, null, 1, ["100"]), new P("new1", "New box", dirId, null, null, 1, []));
        Assert.Equal(200, r.Status);
        Assert.Equal(2, r.Version);
        var db = t.Verify();
        Assert.Equal(["Director", "New box"], db.OrgPositions.Select(p => p.Title).OrderBy(x => x).ToArray());
        Assert.Equal(dirId, db.OrgPositions.Single(p => p.Title == "Director").Id.ToString());
    }

    [Fact]
    public async Task Publish_RejectsStaleVersion_UnlessForced()
    {
        using var t = new TestDb();
        await Publish(t, 0, new P("a", "Director", null, null, null, 1, []));
        Assert.Equal(409, (await Publish(t, 0, new P("a", "Other", null, null, null, 1, []))).Status);
        var forced = await OrgChartEndpoints.PublishAsync(t.Db,
            new Req(0, [new P("a", "Other", null, null, null, 1, [])], Force: true), "ADMIN", Now, CancellationToken.None);
        Assert.Equal(200, forced.Status);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("unknown_parent")]
    [InlineData("unknown_person")]
    [InlineData("unknown_unit")]
    [InlineData("invalid_title")]
    public async Task Publish_RejectsBadShapes_WithoutWriting(string code)
    {
        using var t = new TestDb();
        t.AddUser("100");
        P[] chart = code switch
        {
            "cycle" => [new P("a", "A", "b", null, null, 1, []), new P("b", "B", "a", null, null, 1, [])],
            "unknown_parent" => [new P("a", "A", "zzz", null, null, 1, [])],
            "unknown_person" => [new P("a", "A", null, null, null, 1, ["nope"])],
            "unknown_unit" => [new P("a", "A", null, "NOPE", null, 1, [])],
            _ => [new P("a", "  ", null, null, null, 1, [])],
        };
        var r = await Publish(t, 0, chart);
        Assert.Equal(400, r.Status);
        Assert.Contains(code, System.Text.Json.JsonSerializer.Serialize(r.Error));
        Assert.Empty(t.Verify().OrgPositions);
        Assert.Empty(t.Verify().OrgChartPublications);
    }

    [Fact]
    public async Task Load_ReturnsPositionsWithPeopleInOrder()
    {
        using var t = new TestDb();
        t.AddUser("100"); t.AddUser("200");
        await Publish(t, 0, new P("a", "Team", null, null, null, 1, ["200", "100"]));
        var dto = await OrgChartEndpoints.LoadAsync(t.Verify(), CancellationToken.None);
        Assert.Equal(1, dto.Version);
        Assert.Equal(["200", "100"], dto.Positions.Single().People);
        Assert.Equal(2, dto.People.Count);
    }
}
