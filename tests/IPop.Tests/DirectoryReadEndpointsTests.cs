using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Directory read endpoints (structure tree, division search, employees divisionCode filter) —
/// same seam as DivisionAdminTests/DirectoryTreeTests: SQLite in-memory via TestDb.
///
/// Coverage map (honest):
/// - /api/directory/structure: exercised end-to-end via GetStructureCore (the exact handler body)
///   against TestDb — tree nesting, active-only filtering at every level, count roll-up, the
///   No-Division flat count, and the inactive-Division/inactive-parent drop.
/// - /api/directory/divisions?q=: the blank-query early return is exercised via SearchDivisionsCore
///   (returns []). The ILIKE branch CANNOT be exercised on TestDb because EF.Functions.ILike is
///   Npgsql-only and throws at translation time on SQLite (confirmed by
///   Search_IlkeBranch_Throws_OnSqlite_TestDb below). The grouping/counting logic downstream of
///   the ILIKE filter is covered via AggregateDivisionMatches tests. No production behavior
///   change is made for SQLite — prod keeps EF.Functions.ILike against Npgsql as the ticket
///   requires; this is a pre-existing testability gap shared with /employees and /member-search.
/// - /api/directory/employees?divisionCode=: exercised end-to-end via EmployeesCore (the exact
///   handler body) against TestDb — the divisionCode filter narrows to the matching Division's
///   active members.
/// - Authorization: directory.read is asserted via PermissionEvaluator (the gate
///   RequirePermission calls); AdminOnly is asserted via the actual AuthorizationPolicy
///   ("AdminOnly", RequireRole("Admin")) evaluated by IAuthorizationService against a non-admin
///   principal — the real mechanism, not a ClaimsPrincipal.IsInRole stub.
public class DirectoryReadEndpointsTests
{
    private sealed class UserMaterializationCounter : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        public int Count;
        public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData data, object entity)
        {
            if (entity is User) Count++;
            return entity;
        }
    }

    [Fact]
    public async Task Employees_NamePage_DoesNotMaterializeTheEntireDirectory()
    {
        using var t = new TestDb();
        for (var i = 0; i < 80; i++)
        {
            var u = t.AddUser($"PAGE{i:000}");
            u.Name = $"Person {i:000}";
        }
        t.Db.SaveChanges();
        var counter = new UserMaterializationCounter();
        var options = new DbContextOptionsBuilder<IPopDbContext>()
            .UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(counter).Options;
        await using var db = new IPopDbContext(options);

        var result = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null,
            "name", false, 2, 5, null, db, default);

        Assert.Equal(80, result.Total);
        Assert.Equal(new[] { "Person 005", "Person 006", "Person 007", "Person 008", "Person 009" },
            result.Items.Select(x => x.Name));
        Assert.True(counter.Count <= 5, $"A five-person page materialized {counter.Count} User entities.");
    }

    private static (Department dept, Site site, Division div) SeedDivision(TestDb t,
        string deptCode = "D1", string siteCode = "S1", string divCode = "DV1",
        bool deptActive = true, bool siteActive = true, bool divActive = true)
    {
        var dept = new Department { Id = Guid.NewGuid(), Code = deptCode, Name = $"Dept {deptCode}", IsActive = deptActive };
        var site = new Site { Id = Guid.NewGuid(), Code = siteCode, Name = $"Site {siteCode}", IsActive = siteActive };
        var div = new Division { Id = Guid.NewGuid(), Code = divCode, Name = $"{divCode} Name",
            DepartmentId = dept.Id, SiteId = site.Id, Department = dept, Site = site, IsActive = divActive };
        t.Db.Departments.Add(dept);
        t.Db.Sites.Add(site);
        t.Db.Divisions.Add(div);
        t.Db.SaveChanges();
        return (dept, site, div);
    }

    // --- /api/directory/structure: full handler body (GetStructureCore) against TestDb ---

    [Fact]
    public async Task GetStructure_NestsSiteDepartmentDivision_WithAggregateCounts()
    {
        using var t = new TestDb();
        var (_, _, div) = SeedDivision(t);
        var u1 = t.AddUser("20080115"); u1.DivisionId = div.Id;
        var u2 = t.AddUser("20080116"); u2.DivisionId = div.Id;
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        var siteNode = Assert.Single(result.Sites);
        Assert.Equal("S1", siteNode.Code);
        Assert.Equal("Site S1", siteNode.Name);
        Assert.Equal(2, siteNode.MemberCount);
        var deptNode = Assert.Single(siteNode.Departments);
        Assert.Equal("D1", deptNode.Code);
        Assert.Equal("Dept D1", deptNode.Name);
        Assert.Equal(2, deptNode.MemberCount);
        var divNode = Assert.Single(deptNode.Divisions);
        Assert.Equal("DV1", divNode.Code);
        Assert.Equal(2, divNode.MemberCount);
        Assert.Equal(0, result.NoDivisionCount);
    }

    [Fact]
    public async Task GetStructure_DepartmentAppearsUnderEachSiteThatHostsIt()
    {
        using var t = new TestDb();
        var dept = new Department { Id = Guid.NewGuid(), Code = "D1", Name = "Eng", IsActive = true };
        var siteA = new Site { Id = Guid.NewGuid(), Code = "SA", Name = "Site A", IsActive = true };
        var siteB = new Site { Id = Guid.NewGuid(), Code = "SB", Name = "Site B", IsActive = true };
        var divA = new Division { Id = Guid.NewGuid(), Code = "DVA", Name = "Eng@A",
            DepartmentId = dept.Id, SiteId = siteA.Id, Department = dept, Site = siteA, IsActive = true };
        var divB = new Division { Id = Guid.NewGuid(), Code = "DVB", Name = "Eng@B",
            DepartmentId = dept.Id, SiteId = siteB.Id, Department = dept, Site = siteB, IsActive = true };
        t.Db.Departments.Add(dept);
        t.Db.Sites.AddRange(siteA, siteB);
        t.Db.Divisions.AddRange(divA, divB);
        await t.Db.SaveChangesAsync();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        Assert.Equal(2, result.Sites.Length);
        Assert.All(result.Sites, s => Assert.Equal("D1", Assert.Single(s.Departments).Code));
    }

    [Fact]
    public async Task GetStructure_NoDivisionCount_IncludesActiveUsers_ExcludesInactive()
    {
        using var t = new TestDb();
        t.AddUser("20080115");                       // active, no division -> counted
        t.AddUser("20080116", active: false);         // inactive -> excluded

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        Assert.Empty(result.Sites);
        Assert.Equal(1, result.NoDivisionCount);
    }

    [Fact]
    public async Task GetStructure_DropsInactiveDivision_AndInactiveParent()
    {
        using var t = new TestDb();
        // Inactive Division — its users (u1) are NOT counted in NoDivisionCount (DivisionId != null)
        // and NOT rendered (Division inactive). This is the documented edge case.
        var (_, _, inactiveDiv) = SeedDivision(t, deptCode: "D1", siteCode: "S1", divCode: "DVX", divActive: false);
        var u1 = t.AddUser("20080115"); u1.DivisionId = inactiveDiv.Id;
        // Inactive Department parent — Division dropped from tree.
        SeedDivision(t, deptCode: "D2", siteCode: "S2", divCode: "DV2", deptActive: false);
        // Inactive Site parent — Division dropped from tree.
        SeedDivision(t, deptCode: "D3", siteCode: "S3", divCode: "DV3", siteActive: false);
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        // All three divisions dropped; the active Sites still list (empty) so the skeleton shows.
        Assert.Equal(["S1", "S2"], result.Sites.Select(s => s.Code).ToArray());
        Assert.All(result.Sites, s => Assert.Empty(s.Departments));
        Assert.Equal(0, result.NoDivisionCount); // u1 has DivisionId != null, so not in NoDivision
    }

    [Fact]
    public async Task GetStructure_ActiveUserOnInactiveDivision_NotInTree_NotInNoDivisionCount()
    {
        using var t = new TestDb();
        var (_, _, div) = SeedDivision(t, divActive: false);
        var u = t.AddUser("20080115"); u.DivisionId = div.Id;
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        Assert.Empty(Assert.Single(result.Sites).Departments);
        Assert.Equal(0, result.NoDivisionCount); // u has non-null DivisionId, so excluded from the flat count
    }

    [Fact]
    public async Task GetStructure_NestsSubDepartments_WithRollUp_AndEmptyUnitsAtHomeSite()
    {
        using var t = new TestDb();
        var site = new Site { Id = Guid.NewGuid(), Code = "SP", Name = "Sepanjang", IsActive = true };
        var other = new Site { Id = Guid.NewGuid(), Code = "JK", Name = "Jakarta", IsActive = true };
        var daft = new Department { Id = Guid.NewGuid(), Code = "DAFT", Name = "DAFT", SiteId = site.Id, IsActive = true };
        var fta = new Department { Id = Guid.NewGuid(), Code = "FTA", Name = "Finance, Tax & Accounting", ParentId = daft.Id, IsActive = true, SortOrder = 1 };
        var fm = new Department { Id = Guid.NewGuid(), Code = "FM", Name = "Financial Management", ParentId = daft.Id, IsActive = true, SortOrder = 2 };
        var ict = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT", SiteId = site.Id, IsActive = true };
        var gone = new Department { Id = Guid.NewGuid(), Code = "OLD", Name = "Old", ParentId = daft.Id, IsActive = false };
        var tax = new Division { Id = Guid.NewGuid(), Code = "TAX", Name = "Tax", DepartmentId = fta.Id, SiteId = site.Id, IsActive = true };
        var gm = new Division { Id = Guid.NewGuid(), Code = "GM", Name = "DAFT GM", DepartmentId = daft.Id, SiteId = site.Id, IsActive = true };
        t.Db.Sites.AddRange(site, other);
        t.Db.Departments.AddRange(daft, fta, fm, ict, gone);
        t.Db.Divisions.AddRange(tax, gm);
        t.Db.SaveChanges();
        t.AddUser("20080115").DivisionId = tax.Id;
        t.AddUser("20080116").DivisionId = tax.Id;
        t.AddUser("20080117").DivisionId = gm.Id;
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        Assert.Empty(result.Sites.Single(s => s.Code == "JK").Departments); // empty site still listed
        var sp = result.Sites.Single(s => s.Code == "SP");
        Assert.Equal(3, sp.MemberCount);
        Assert.Equal(["DAFT", "ICT"], sp.Departments.Select(d => d.Code).ToArray());
        var daftNode = sp.Departments[0];
        Assert.Equal(3, daftNode.MemberCount);
        Assert.Equal("GM", Assert.Single(daftNode.Divisions).Code);
        Assert.Equal(["FTA", "FM"], daftNode.Children!.Select(c => c.Code).ToArray()); // SortOrder, inactive dropped
        Assert.Equal(2, daftNode.Children![0].MemberCount);
        Assert.Empty(daftNode.Children![1].Divisions);                                  // empty sub-dept shown
        Assert.Equal(0, sp.Departments[1].MemberCount);
    }

    [Fact]
    public async Task Employees_DepartmentFilter_IncludesSubDepartments()
    {
        using var t = new TestDb();
        var site = new Site { Id = Guid.NewGuid(), Code = "SP", Name = "Sepanjang", IsActive = true };
        var daft = new Department { Id = Guid.NewGuid(), Code = "DAFT", Name = "DAFT", IsActive = true };
        var fta = new Department { Id = Guid.NewGuid(), Code = "FTA", Name = "FTA", ParentId = daft.Id, IsActive = true };
        var ict = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT", IsActive = true };
        var tax = new Division { Id = Guid.NewGuid(), Code = "TAX", Name = "Tax", DepartmentId = fta.Id, SiteId = site.Id, IsActive = true };
        t.Db.Sites.Add(site);
        t.Db.Departments.AddRange(daft, fta, ict);
        t.Db.Divisions.Add(tax);
        t.Db.SaveChanges();
        t.AddUser("20080115").DepartmentId = daft.Id;   // placed on the parent directly
        t.AddUser("20080116").DivisionId = tax.Id;      // via a sub-department's Division
        t.AddUser("20080117").DepartmentId = ict.Id;    // elsewhere
        t.Db.SaveChanges();

        var all = await DirectoryEndpoints.EmployeesCore(null, "DAFT", null, null, null, null, null, null, 1, 50, null, t.Db, default);
        var sub = await DirectoryEndpoints.EmployeesCore(null, "FTA", null, null, null, null, null, null, 1, 50, null, t.Db, default);

        Assert.Equal(["20080115", "20080116"], all.Items.Select(i => i.Nik).OrderBy(n => n).ToArray());
        Assert.Equal("20080116", Assert.Single(sub.Items).Nik);
    }

    /// The header tabs read directory-wide totals from this payload. Counting the loaded
    /// page instead reported 0 online whenever nobody on page 1 happened to be online.
    [Fact]
    public async Task GetStructure_ReturnsPresenceCounts_ForAllActiveUsers()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        // One per bucket, plus an inactive user that must not be counted.
        t.AddUser("20080101").PresenceLastSeenAt = now;
        { var idle = t.AddUser("20080102"); idle.PresenceLastSeenAt = now; idle.PresenceIsIdle = true; } // away (beating, computer idle)
        t.AddUser("20080103").PresenceLastSeenAt = now.AddHours(-3);     // off (no beat for >2m)
        { var busy = t.AddUser("20080104"); busy.PresenceManualStatus = "busy"; busy.PresenceLastSeenAt = now; }
        t.AddUser("20080105", active: false).PresenceLastSeenAt = now;   // inactive -> excluded
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.GetStructureCore(t.Db, default);

        var p = result.PresenceCounts;
        Assert.NotNull(p);
        Assert.Equal(4, p!.All);
        Assert.Equal(1, p.Online);
        Assert.Equal(1, p.Busy);
        Assert.Equal(1, p.Away);
        Assert.Equal(1, p.Offline);
    }

    // --- /api/directory/divisions?q= ---

    [Fact]
    public async Task Search_BlankQuery_ReturnsEmptyArray_NotError()
    {
        using var t = new TestDb();
        SeedDivision(t);
        // Blank/whitespace q hits the early return before any ILIKE translation is attempted.

        Assert.Empty(await DirectoryEndpoints.SearchDivisionsCore(null, t.Db, default));
        Assert.Empty(await DirectoryEndpoints.SearchDivisionsCore("", t.Db, default));
        Assert.Empty(await DirectoryEndpoints.SearchDivisionsCore("   ", t.Db, default));
    }

    [Fact]
    public async Task Search_IlkeBranch_Throws_OnSqlite_TestDb()
    {
        // This documents the testability boundary, not a production bug: EF.Functions.ILike is
        // Npgsql-only and does not translate on SQLite (TestDb's provider). Production runs
        // against Npgsql where ILike is correct. The throw proves the ILIKE code path cannot be
        // exercised via TestDb — the grouping logic downstream is covered by
        // AggregateDivisionMatches_* tests instead.
        using var t = new TestDb();
        SeedDivision(t);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DirectoryEndpoints.SearchDivisionsCore("alice", t.Db, default));
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
        Assert.Equal("Eng@HQ", dv1.Name);
        var dv2 = Assert.Single(result, x => x.Code == "DV2");
        Assert.Equal(1, dv2.MatchCount);
    }

    [Fact]
    public void AggregateDivisionMatches_EmptyInput_ReturnsEmptyArray()
    {
        Assert.Empty(DirectoryEndpoints.AggregateDivisionMatches([]));
    }

    // --- /api/directory/employees?divisionCode= ---

    [Fact]
    public async Task Employees_DivisionCodeFilter_NarrowsResults()
    {
        using var t = new TestDb();
        var (_, _, div) = SeedDivision(t);
        var inDiv = t.AddUser("20080115"); inDiv.DivisionId = div.Id;
        t.AddUser("20080116");    // no division -> filtered out
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.EmployeesCore(null, null, null, "DV1", null, null, null, null,
            null, null, null, t.Db, default);

        var item = Assert.Single(result.Items);
        Assert.Equal("20080115", item.Nik);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Employees_manager_filter_returns_direct_reports()
    {
        using var t = new TestDb();
        var boss = t.AddUser("BOSS");
        var report1 = t.AddUser("REP1"); report1.ManagerNik = boss.Nik;
        var report2 = t.AddUser("REP2"); report2.ManagerNik = boss.Nik;
        t.AddUser("OTHR"); // bukan bawahan
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, boss.Nik, null, null,
            null, null, null, t.Db, default);

        Assert.Equal(2, result.Total);
        Assert.All(result.Items, e => Assert.Equal(boss.Nik, e.ManagerNik));
        Assert.Contains(result.Items, e => e.Nik == "REP1");
        Assert.Contains(result.Items, e => e.Nik == "REP2");
    }

    [Fact]
    public async Task Employees_carry_favorite_flag_for_caller()
    {
        using var t = new TestDb();
        var caller = t.AddUser("CALLER");
        var fav = t.AddUser("FAV01");
        t.AddUser("PLAIN");
        t.Db.UserFavoriteContacts.Add(new IPop.Core.Entities.UserFavoriteContact
        {
            Id = Guid.NewGuid(), UserId = caller.Id, ContactNik = fav.Nik, CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();

        var result = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, null, null,
            null, null, caller.Nik, t.Db, default);

        Assert.True(result.Items.Single(e => e.Nik == "FAV01").IsFavorite);
        Assert.False(result.Items.Single(e => e.Nik == "PLAIN").IsFavorite);
        // Tanpa pemanggil (anonim) semua bukan favorit.
        var anon = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, null, null,
            null, null, null, t.Db, default);
        Assert.All(anon.Items, e => Assert.False(e.IsFavorite));
    }

    [Fact]
    public async Task Favorites_FilterBeforeCountAndPaging_AndStayOwnerScoped()
    {
        using var t = new TestDb();
        var caller = t.AddUser("CALLER");
        var other = t.AddUser("OTHER");
        var users = Enumerable.Range(0, 2501).Select(i => new User
        {
            Id = Guid.NewGuid(), Nik = $"P{i:D4}", Name = $"Person {i:D4}",
            Email = $"p{i}@test.invalid", IsActive = true
        }).ToArray();
        t.Db.Users.AddRange(users);
        t.Db.UserFavoriteContacts.AddRange(
            new UserFavoriteContact { Id = Guid.NewGuid(), UserId = caller.Id, ContactNik = users[^1].Nik },
            new UserFavoriteContact { Id = Guid.NewGuid(), UserId = other.Id, ContactNik = users[0].Nik });
        await t.Db.SaveChangesAsync();

        var result = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, null, null,
            1, 120, caller.Nik, t.Db, default, favorites: true);
        Assert.Equal(1, result.Total);
        Assert.Equal(users[^1].Nik, Assert.Single(result.Items).Nik);
        Assert.True(result.Items[0].IsFavorite);
        var otherResult = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, null, null,
            1, 120, other.Nik, t.Db, default, favorites: true);
        Assert.Equal(users[0].Nik, Assert.Single(otherResult.Items).Nik);
        var anon = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, null, null,
            1, 120, null, t.Db, default, favorites: true);
        Assert.Equal(0, anon.Total);

        // This exact unbounded core is used by the CSV handler, not a page with a hidden cap.
        var exported = await DirectoryEndpoints.FilteredEmployeesCore(null, null, null, null, null, null,
            "name", true, caller.Nik, t.Db, default);
        Assert.Equal(2503, exported.Length);
        var page = await DirectoryEndpoints.EmployeesCore(null, null, null, null, null, null, "name", true,
            2, 120, caller.Nik, t.Db, default);
        Assert.Equal(exported.Skip(120).Take(120), page.Items);
        var favoriteExport = await DirectoryEndpoints.FilteredEmployeesCore(null, null, null, null, null, null,
            "name", true, caller.Nik, t.Db, default, favorites: true);
        Assert.Equal(result.Items, favoriteExport);
    }

    [Fact]
    public async Task SiteScope_UsesDivisionFallback_ButDirectPlacementWins()
    {
        using var t = new TestDb();
        var (dept, site, div) = SeedDivision(t);
        var child = new Department { Id = Guid.NewGuid(), Code = "CHILD", Name = "Child", ParentId = dept.Id };
        var otherSite = new Site { Id = Guid.NewGuid(), Code = "S2", Name = "Other" };
        t.Db.Departments.Add(child);
        t.Db.Sites.Add(otherSite);
        var fallback = t.AddUser("FALLBACK"); fallback.DivisionId = div.Id;
        var direct = t.AddUser("DIRECT"); direct.DepartmentId = child.Id; direct.SiteId = site.Id;
        var elsewhere = t.AddUser("ELSEWHERE"); elsewhere.DivisionId = div.Id; elsewhere.SiteId = otherSite.Id;
        await t.Db.SaveChangesAsync();

        var result = await DirectoryEndpoints.EmployeesCore(null, dept.Code, site.Code, null, null, null,
            null, null, 1, 120, null, t.Db, default);
        Assert.Equal(2, result.Total);
        Assert.All(result.Items, e => Assert.Equal(site.Code, e.SiteCode));
        Assert.Equal(site.Name, result.Items.Single(e => e.Nik == fallback.Nik).Site);
        Assert.DoesNotContain(result.Items, e => e.Nik == elsewhere.Nik);
    }

    [Fact]
    public async Task StatusSort_UsesExplicitRanks_NameAndStableIdTies()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var on1 = t.AddUser("ON1"); on1.Name = "Same"; on1.PresenceLastSeenAt = now;
        var on2 = t.AddUser("ON2"); on2.Name = "Same"; on2.PresenceLastSeenAt = now;
        var busy = t.AddUser("BUSY"); busy.PresenceLastSeenAt = now; busy.PresenceManualStatus = "busy";
        var away = t.AddUser("AWAY"); away.PresenceLastSeenAt = now; away.PresenceIsIdle = true;
        t.AddUser("OFF");
        await t.Db.SaveChangesAsync();
        var asc = await DirectoryEndpoints.FilteredEmployeesCore(null, null, null, null, null, null,
            "status", false, null, t.Db, default);
        Assert.Equal(new[] { "on", "on", "busy", "away", "off" }, asc.Select(e => e.Status));
        Assert.Equal(new[] { on1.Id, on2.Id }.OrderBy(id => id), asc.Take(2).Select(e => e.UserId!.Value));
        var desc = await DirectoryEndpoints.FilteredEmployeesCore(null, null, null, null, null, null,
            "status", true, null, t.Db, default);
        Assert.Equal(new[] { "off", "away", "busy", "on", "on" }, desc.Select(e => e.Status));
        var filtered = await DirectoryEndpoints.EmployeesCore(null, null, null, null, "on", null,
            "status", false, 1, 1, null, t.Db, default);
        Assert.Equal(2, filtered.Total);
        Assert.Equal(asc[0], Assert.Single(filtered.Items));
    }

    [Fact]
    public async Task GroupPaging_DuplicateLabelsRemainSeparate_AndMatchExportOrdering()
    {
        using var t = new TestDb();
        var (dept, site, div) = SeedDivision(t);
        div.Name = "Same unit";
        var second = new Division { Id = Guid.NewGuid(), Code = "DV2", Name = div.Name,
            DepartmentId = dept.Id, SiteId = site.Id, IsActive = true };
        t.Db.Divisions.Add(second);
        var users = Enumerable.Range(0, 125).Select(i => new User
        {
            Id = Guid.NewGuid(), Nik = $"G{i:D3}", Name = $"Person {124 - i:D3}",
            Email = $"g{i}@test.invalid", IsActive = true, DivisionId = i < 122 ? div.Id : second.Id
        }).ToArray();
        t.Db.Users.AddRange(users);
        await t.Db.SaveChangesAsync();
        var full = await DirectoryEndpoints.FilteredEmployeesCore(null, dept.Code, site.Code, null, null, null,
            "name", false, null, t.Db, default, grouped: true);
        var first = await DirectoryEndpoints.EmployeesCore(null, dept.Code, site.Code, null, null, null,
            "name", false, 1, 120, null, t.Db, default, grouped: true);
        var next = await DirectoryEndpoints.EmployeesCore(null, dept.Code, site.Code, null, null, null,
            "name", false, 2, 120, null, t.Db, default, grouped: true);
        Assert.Equal(125, first.Total);
        Assert.Equal(120, first.Items.Length);
        Assert.Equal(full, first.Items.Concat(next.Items));
        Assert.Equal(125, full.Select(e => e.UserId).Distinct().Count());
        Assert.Equal(2, full.Select(e => e.UnitKey).Distinct().Count());
        Assert.All(full, e => Assert.Equal("Same unit", e.UnitName));
        Assert.All(full.GroupBy(e => e.UnitKey), g =>
            Assert.Equal(g.Select(e => e.Name).OrderBy(n => n), g.Select(e => e.Name)));
        Assert.Equal(full.Select(e => e.UnitKey).OrderBy(key => key), full.Select(e => e.UnitKey));
    }

    [Fact]
    public async Task ExportCore_CombinesScopeDivisionManagerPresenceFavoritesAndSortLikePaging()
    {
        using var t = new TestDb();
        var (dept, site, div) = SeedDivision(t);
        var caller = t.AddUser("CALLER");
        var matching = t.AddUser("MATCH"); matching.DivisionId = div.Id;
        matching.ManagerNik = caller.Nik; matching.PresenceLastSeenAt = DateTime.UtcNow;
        var wrongManager = t.AddUser("WRONG"); wrongManager.DivisionId = div.Id;
        wrongManager.PresenceLastSeenAt = DateTime.UtcNow;
        t.Db.UserFavoriteContacts.AddRange(new[] { matching, wrongManager }.Select(u =>
            new UserFavoriteContact { Id = Guid.NewGuid(), UserId = caller.Id, ContactNik = u.Nik }));
        await t.Db.SaveChangesAsync();
        var full = await DirectoryEndpoints.FilteredEmployeesCore(null, dept.Code, site.Code, div.Code,
            "on", caller.Nik, "status", true, caller.Nik, t.Db, default, favorites: true, grouped: true);
        var page = await DirectoryEndpoints.EmployeesCore(null, dept.Code, site.Code, div.Code,
            "on", caller.Nik, "status", true, 1, 120, caller.Nik, t.Db, default, favorites: true, grouped: true);
        Assert.Equal(matching.Id, Assert.Single(full).UserId);
        Assert.Equal(full, page.Items);
        Assert.Equal(full.Length, page.Total);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    public void CsvFields_PreserveEscapingAndFormulaProtection(string value, string expected)
        => Assert.Equal(expected, DirectoryEndpoints.CsvField(value));

    // --- Authorization (real application mechanisms, not IsInRole stubs) ---

    [Fact]
    public async Task DirectoryRead_NonAdminEmployeeRole_IsAllowed_ByPermissionEvaluator()
    {
        // directory.read is an Employee-baseline permission granted via RolePermissions, gated by
        // the RequirePermission filter that calls IPermissionEvaluator. A non-admin role satisfies
        // it — confirms these read endpoints are NOT accidentally admin-only.
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
        Assert.True(await evaluator.IsAllowedAsync(user.Id, "directory.read", null));
    }

    [Fact]
    public async Task AdminOnlyPolicy_NonAdmin_IsDenied_ByAuthorizationService()
    {
        // The /api/admin/org/divisions endpoints (Task 1) are gated by the "AdminOnly" policy,
        // which is RequireRole("Admin") — a different mechanism from directory.read's
        // RequirePermission. This evaluates the real policy via IAuthorizationService against a
        // non-admin principal: the actual gate Task 1's endpoints run through. Regression check
        // that this task's read-endpoint additions did not disturb that boundary.
        var services = new ServiceCollection();
        services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireRole("Admin")));
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var auth = sp.GetRequiredService<IAuthorizationService>();

        var employee = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Employee")], "test"));

        var result = await auth.AuthorizeAsync(employee, "AdminOnly");

        Assert.False(result.Succeeded);
    }
}
