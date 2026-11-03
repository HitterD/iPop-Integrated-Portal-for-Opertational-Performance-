using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Approval;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Workbook Division/Department/Bagian/Work Location → the existing Plant › Division › Department tree
/// (DB Site › Department › Division). Planning is read-only; Apply writes.
public class TreePlannerTests
{
    private static WorkbookRow Row(string nik, string division, string department, string bagian, string location = "SJA-1") =>
        new(2, nik, $"User {nik}", division, department, department, bagian, null, null, location, new string?[9]);

    /// The prod-like starting tree: plants + top Divisions + a few existing sub-units.
    private static TestDb Seed()
    {
        var t = new TestDb();
        var sep = new Site { Id = Guid.NewGuid(), Code = "SJA 1", Name = "SJA SEPANJANG" };
        var krw = new Site { Id = Guid.NewGuid(), Code = "SJA-KRW", Name = "SJA KARAWANG" };
        var skd = new Site { Id = Guid.NewGuid(), Code = "SJA-SKD", Name = "SJA SUKODONO" };
        var smg = new Site { Id = Guid.NewGuid(), Code = "SJA-SMG", Name = "SJA SEMARANG" };
        var jkt = new Site { Id = Guid.NewGuid(), Code = "SJA-JKT", Name = "SJA JAKARTA" };
        t.Db.Sites.AddRange(sep, krw, skd, smg, jkt);
        var ops = new Department { Id = Guid.NewGuid(), Code = "OPS", Name = "Operational", SiteId = sep.Id };
        var hc = new Department { Id = Guid.NewGuid(), Code = "HC", Name = "Human Capital", SiteId = sep.Id };
        var p1 = new Department { Id = Guid.NewGuid(), Code = "OPS-P1", Name = "Production 1", ParentId = ops.Id };
        var plan = new Department { Id = Guid.NewGuid(), Code = "OPS-PLAN", Name = "Plant", ParentId = ops.Id };
        var main = new Department { Id = Guid.NewGuid(), Code = "OPS-PLAN-MAIN", Name = "Maintenance", ParentId = plan.Id };
        var qa = new Department { Id = Guid.NewGuid(), Code = "OPS-QA", Name = "Quality Assurance", ParentId = ops.Id };
        t.Db.Departments.AddRange(ops, hc, p1, plan, main, qa);
        t.Db.Divisions.Add(new Division { Id = Guid.NewGuid(), Code = "OPS-P1-HEAD", Name = "Head", DepartmentId = p1.Id, SiteId = sep.Id });
        t.Db.SaveChanges();
        return t;
    }

    private static Task<TreePlan> Plan(TestDb t, params WorkbookRow[] rows) => new TreePlanner(t.Db).PlanAsync(rows, default);

    [Fact]
    public async Task Plan_MapsWorkLocationToSite()
    {
        using var t = Seed(); t.AddUser("E1");
        var plan = await Plan(t, Row("E1", "OPERATION SJA 2", "PRODUCTION 1", "PRODUCTION 1", "SJA-2"));
        Assert.Equal("SJA-KRW", Assert.Single(plan.Moves).SiteCode);
    }

    [Fact]
    public async Task Plan_ReusesExistingDepartmentCaseInsensitive()
    {
        using var t = Seed(); t.AddUser("E1");
        var plan = await Plan(t, Row("E1", "OPERATION SJA 1", "PRODUCTION 1", "PRODUCTION 1"));
        Assert.DoesNotContain(plan.Creates, c => c.Kind == "department");
        var move = Assert.Single(plan.Moves);
        Assert.Equal("OPS-P1", move.DepartmentCode);
        Assert.Null(move.DivisionCode);   // Bagian == Department → no unit
    }

    [Fact]
    public async Task Plan_UsesAliasAndDescendantMatch()
    {
        using var t = Seed(); t.AddUser("E1"); t.AddUser("E2");
        var plan = await Plan(t,
            Row("E1", "OPERATION", "QA", "QA"),
            Row("E2", "OPERATION SJA 1", "MAINTENANCE", "MAINTENANCE"));
        Assert.Equal("OPS-QA", plan.Moves.Single(m => m.Nik == "E1").DepartmentCode);
        Assert.Equal("OPS-PLAN-MAIN", plan.Moves.Single(m => m.Nik == "E2").DepartmentCode);
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public async Task Plan_CreatesMissingDepartmentAndDivisionWithHouseCodes()
    {
        using var t = Seed(); t.AddUser("E1"); t.AddUser("E2");
        var plan = await Plan(t,
            Row("E1", "OPERATION SJA 1", "PRODUCTION 1", "PACKING AB"),
            Row("E2", "HUMAN CAPITAL", "HCBP AREA KARAWANG", "HC SERVICES AREA - KARAWANG", "SJA-2"));

        var packing = plan.Creates.Single(c => c.Kind == "division" && c.Name == "Packing AB");
        Assert.Equal("OPS-P1", packing.ParentCode);
        Assert.Equal("SJA 1", packing.SiteCode);
        Assert.StartsWith("OPS-P1-", packing.Code);

        var hcbp = plan.Creates.Single(c => c.Kind == "department" && c.Name == "HCBP Area Karawang");
        Assert.Equal("HC", hcbp.ParentCode);
        var area = plan.Creates.Single(c => c.Kind == "division" && c.ParentCode == hcbp.Code);
        Assert.Equal("HC Services Area", area.Name);           // location suffix dropped
        Assert.Contains("-KRW-", area.Code);                   // site infix like HC-HS-KRW-STAF
        Assert.Equal(plan.Creates.Select(c => c.Code).Distinct().Count(), plan.Creates.Count);
    }

    [Fact]
    public async Task Plan_SemarangPowderNestsUnderOps()
    {
        using var t = Seed(); t.AddUser("E1");
        var plan = await Plan(t, Row("E1", "OPERATION SJA SEMARANG POWDER", "PRODUCTION - R&G", "PACKING - R&G", "SJA-SEMARANG"));
        var powder = plan.Creates.Single(c => c.Kind == "department" && c.Name == "Powder");
        Assert.Equal("OPS", powder.ParentCode);
        Assert.Equal("SJA-SMG", powder.SiteCode);
        var prod = plan.Creates.Single(c => c.Kind == "department" && c.Name == "Production - R&G");
        Assert.Equal(powder.Code, prod.ParentCode);
        Assert.Equal("Operational › Powder › Production - R&G", prod.Path);
        var move = Assert.Single(plan.Moves);
        Assert.Equal("SJA-SMG", move.SiteCode);
        Assert.Equal(prod.Code, move.DepartmentCode);
        Assert.Equal("Packing - R&G", plan.Creates.Single(c => c.Code == move.DivisionCode).Name);
    }

    [Fact]
    public async Task Plan_RoleLikeDepartment_SitsOnTopDivision()
    {
        using var t = Seed(); t.AddUser("E1"); t.AddUser("E2");
        var plan = await Plan(t,
            Row("E1", "OPERATION SJA 1", "PLANT MANAGER", "PLANT 1"),
            Row("E2", "OPERATION", "OPERATIONAL GENERAL MANAGER", "OPERATION"));
        Assert.All(plan.Moves, m => { Assert.Equal("OPS", m.DepartmentCode); Assert.Null(m.DivisionCode); });
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public async Task Plan_OverridesManualPlacement_AndSkipsUnchanged()
    {
        using var t = Seed();
        var u = t.AddUser("E1"); var same = t.AddUser("E2");
        var hc = t.Db.Departments.Single(d => d.Code == "HC");
        var p1 = t.Db.Departments.Single(d => d.Code == "OPS-P1");
        var sep = t.Db.Sites.Single(s => s.Code == "SJA 1");
        u.DepartmentId = hc.Id; u.SiteId = sep.Id;
        same.DepartmentId = p1.Id; same.SiteId = sep.Id;
        t.Db.SaveChanges();

        var plan = await Plan(t,
            Row("E1", "OPERATION SJA 1", "PRODUCTION 1", "PRODUCTION 1"),
            Row("E2", "OPERATION SJA 1", "PRODUCTION 1", "PRODUCTION 1"));
        var move = Assert.Single(plan.Moves);
        Assert.Equal("E1", move.Nik);
        Assert.Equal("Human Capital", move.FromPath);
        Assert.Contains("Production 1", move.ToPath);
    }

    [Fact]
    public async Task Plan_DeactivatesEmptiedGenericUnits_NotOthers()
    {
        using var t = Seed();
        var head = t.Db.Divisions.Single(d => d.Code == "OPS-P1-HEAD");
        var u = t.AddUser("E1"); u.DivisionId = head.Id; u.DepartmentId = head.DepartmentId; t.Db.SaveChanges();
        var main = t.Db.Departments.Single(d => d.Code == "OPS-PLAN-MAIN");
        t.Db.Divisions.Add(new Division { Id = Guid.NewGuid(), Code = "OPS-MAIN-X", Name = "Boiler", DepartmentId = main.Id });
        t.Db.SaveChanges();

        var plan = await Plan(t, Row("E1", "OPERATION SJA 1", "PRODUCTION 1", "PACKING AB"));
        var d = Assert.Single(plan.Deactivations);
        Assert.Equal("OPS-P1-HEAD", d.Code);
    }

    [Fact]
    public async Task Plan_UnknownDivision_GoesToUnmapped()
    {
        using var t = Seed(); t.AddUser("E1");
        var plan = await Plan(t, Row("E1", "LEGAL", "LEGAL", "LEGAL"));
        Assert.Empty(plan.Moves);
        Assert.Contains(plan.Unmapped, s => s.Contains("LEGAL"));
    }

    [Fact]
    public async Task Plan_SkipsNiksWithoutAccount()
    {
        using var t = Seed();
        var plan = await Plan(t, Row("E404", "OPERATION SJA 1", "PRODUCTION 1", "PACKING AB"));
        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Creates);
    }

    [Fact]
    public async Task Apply_CreatesThenPlaces()
    {
        using var t = Seed(); t.AddUser("E1");
        var planner = new TreePlanner(t.Db);
        var plan = await planner.PlanAsync([Row("E1", "OPERATION SJA SEMARANG POWDER", "PRODUCTION - R&G", "PACKING - R&G", "SJA-SEMARANG")], default);
        await planner.ApplyAsync(plan, default);
        await t.Db.SaveChangesAsync();

        var db = t.Verify();
        var u = db.Users.Include(x => x.Division).Include(x => x.Department).Include(x => x.Site).Single(x => x.Nik == "E1");
        Assert.Equal("Packing - R&G", u.Division!.Name);
        Assert.Equal("Production - R&G", u.Department!.Name);
        Assert.Equal("SJA-SMG", u.Site!.Code);
        Assert.Equal(u.SiteId, u.Division.SiteId);
        Assert.Equal("Powder", db.Departments.Single(d => d.Id == u.Department.ParentId).Name);
    }

    [Fact]
    public async Task Apply_ThenReplan_HasNoChanges()
    {
        using var t = Seed();
        // Existing "Maintenance" sits under Plant; the RTD line gets its own Maintenance on first apply.
        foreach (var n in new[] { "E1", "E2", "E3", "E4" }) t.AddUser(n);
        WorkbookRow[] rows =
        [
            Row("E1", "OPERATION SJA 3", "MAINTENANCE", "PROCESS MAINTENANCE", "SJA-3"),
            Row("E2", "OPERATION SJA SEMARANG RTD", "MAINTENANCE", "MAINTENANCE - RTD", "SJA-SEMARANG"),
            Row("E3", "HUMAN CAPITAL", "HC COMPENSATION & BENEFIT", "PAYROLL & BENEFIT - SEPANJANG"),
            Row("E4", "HUMAN CAPITAL", "HC COMPENSATION & BENEFIT", "RETIREMENT"),
        ];
        var planner = new TreePlanner(t.Db);
        await planner.ApplyAsync(await planner.PlanAsync(rows, default), default);
        await t.Db.SaveChangesAsync();

        var again = await new TreePlanner(t.Verify()).PlanAsync(rows, default);
        Assert.Empty(again.Creates);
        Assert.Empty(again.Moves);
    }

    [Fact]
    public async Task Plan_DepartmentNameMatchingExistingUnit_ReusesItInsteadOfDuplicating()
    {
        using var t = Seed(); t.AddUser("E1");
        var hc = t.Db.Departments.Single(d => d.Code == "HC");
        var sep = t.Db.Sites.Single(s => s.Code == "SJA 1");
        t.Db.Divisions.Add(new Division { Id = Guid.NewGuid(), Code = "HC-HD", Name = "HC Development", DepartmentId = hc.Id, SiteId = sep.Id });
        t.Db.SaveChanges();

        var plan = await Plan(t, Row("E1", "HUMAN CAPITAL", "HC DEVELOPMENT", "TALENT ACQUISITION & ASSESSMENT"));

        Assert.Empty(plan.Creates);
        var move = Assert.Single(plan.Moves);
        Assert.Equal("HC", move.DepartmentCode);
        Assert.Equal("HC-HD", move.DivisionCode);
    }

    [Fact]
    public async Task Plan_BagianMatchingChildDepartment_UsesIt()
    {
        using var t = Seed(); t.AddUser("E1"); t.AddUser("E2");
        var ops = t.Db.Departments.Single(d => d.Code == "OPS");
        var logi = new Department { Id = Guid.NewGuid(), Code = "OPS-LOGI", Name = "Logistic", ParentId = ops.Id };
        t.Db.Departments.AddRange(logi,
            new Department { Id = Guid.NewGuid(), Code = "OPS-LOGI-TO", Name = "Transport Operational", ParentId = logi.Id },
            new Department { Id = Guid.NewGuid(), Code = "OPS-LOGI-EXIM", Name = "Exim", ParentId = logi.Id });
        t.Db.SaveChanges();

        var plan = await Plan(t,
            Row("E1", "OPERATION", "LOGISTIC", "TRANSPORT OPERATION"),
            Row("E2", "OPERATION", "LOGISTIC", "EXIM", "SJA-JAKARTA"));

        Assert.Empty(plan.Creates);
        Assert.Equal("OPS-LOGI-TO", plan.Moves.Single(m => m.Nik == "E1").DepartmentCode);
        Assert.Null(plan.Moves.Single(m => m.Nik == "E1").DivisionCode);
        Assert.Equal("OPS-LOGI-EXIM", plan.Moves.Single(m => m.Nik == "E2").DepartmentCode);
    }

    [Fact]
    public async Task Plan_ChainsOnlyFile_ProposesNoDirectoryChanges()
    {
        using var t = Seed(); t.AddUser("E1");
        // An emptied generic unit exists, but this file carries no Division/Department columns.
        var row = new WorkbookRow(2, "E1", "E1", null, null, null, null, null, null, null, new string?[9]);

        var plan = await Plan(t, row);

        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Deactivations);
    }
}
