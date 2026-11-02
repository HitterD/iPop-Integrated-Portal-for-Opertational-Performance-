using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Approval;
using Xunit;

namespace IPop.Tests;

/// Workbook → preview plan → apply in one transaction.
public class ApprovalImportTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    private static WorkbookRow Row(string nik, int rowNumber = 2, params (int Slot, string Nik)[] slots)
    {
        var s = new string?[9];
        foreach (var (slot, n) in slots) s[slot - 1] = n;
        return new WorkbookRow(rowNumber, nik, $"User {nik}", null, null, null, null, "Technician", "STAFF", null, s);
    }
    private static WorkbookParseResult Parsed(params WorkbookRow[] rows) => new(rows, 0, []);
    private static ApprovalImport Import(TestDb t) => new(t.Db, new ApprovalService(t.Db, () => Now), new TreePlanner(t.Db));

    private static void SeedChain(TestDb t, string nik, params (int Slot, string Nik)[] slots)
    {
        var c = new ApproverChain { Nik = nik, UpdatedAt = Now };
        foreach (var (slot, n) in slots) c.SetSlot(slot, n);
        t.Db.ApproverChains.Add(c);
        t.Db.SaveChanges();
    }

    [Fact]
    public async Task Plan_ClassifiesNewChangedUnchanged()
    {
        using var t = new TestDb();
        foreach (var n in new[] { "E1", "E2", "E3", "M", "N" }) t.AddUser(n);
        SeedChain(t, "E2", (1, "M"));
        SeedChain(t, "E3", (1, "M"));

        var plan = await Import(t).PlanAsync(Parsed(Row("E1", 2, (1, "M")), Row("E2", 3, (1, "N")), Row("E3", 4, (1, "M"))), false, "tok", default);

        Assert.Equal("new", plan.Chains.Single(c => c.Nik == "E1").Kind);
        var changed = plan.Chains.Single(c => c.Nik == "E2");
        Assert.Equal("changed", changed.Kind);
        Assert.Equal("M", changed.Before![0]);
        Assert.Equal("N", changed.After[0]);
        Assert.DoesNotContain(plan.Chains, c => c.Nik == "E3");
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public async Task Plan_DuplicateNik_LastWinsAndWarns()
    {
        using var t = new TestDb();
        t.AddUser("E1");
        var plan = await Import(t).PlanAsync(Parsed(Row("E1", 2, (1, "A")), Row("E1", 9, (1, "B"))), false, "tok", default);
        Assert.Equal("B", Assert.Single(plan.Chains).After[0]);
        Assert.Contains(plan.Warnings, w => w.Contains("E1") && w.Contains("9"));
    }

    [Fact]
    public async Task Plan_ListsUnknownEmployeesAndInactiveApprovers()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("GONE", active: false);
        var plan = await Import(t).PlanAsync(Parsed(Row("E1", 2, (1, "GONE"), (2, "NOBODY")), Row("E404", 3, (1, "GONE"))), false, "tok", default);
        Assert.Equal(new[] { "E404" }, plan.UnknownEmployeeNiks);
        Assert.Equal(new[] { "GONE" }, plan.InactiveApproverNiks);
        Assert.Equal(new[] { "NOBODY" }, plan.UnknownApproverNiks);
        Assert.Equal(2, plan.Chains.Count);   // chains are kept even for people without an account
    }

    [Fact]
    public async Task Plan_CompleteFile_ClearsMissing_ElseLeavesThem()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("E2");
        SeedChain(t, "E2", (1, "M"));

        var partial = await Import(t).PlanAsync(Parsed(Row("E1", 2, (1, "M"))), false, "tok", default);
        Assert.DoesNotContain(partial.Chains, c => c.Nik == "E2");

        var complete = await Import(t).PlanAsync(Parsed(Row("E1", 2, (1, "M"))), true, "tok", default);
        var cleared = complete.Chains.Single(c => c.Nik == "E2");
        Assert.Equal("cleared", cleared.Kind);
        Assert.All(cleared.After, Assert.Null);
    }

    [Fact]
    public async Task Apply_WritesChainsManagerNikAndAudit()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("M");
        var import = Import(t);
        var plan = await import.PlanAsync(Parsed(Row("E1", 2, (1, "M"), (8, "G"))), false, "tok", default);

        var result = await import.ApplyAsync(plan, "HC1", default);

        Assert.Equal(1, result.ChainsWritten);
        var db = t.Verify();
        var chain = db.ApproverChains.Single(c => c.Nik == "E1");
        Assert.Equal("G", chain.Slot8Nik);
        Assert.Equal("Technician", chain.SourceJobTitle);
        Assert.Equal("M", db.Users.Single(u => u.Nik == "E1").ManagerNik);
        Assert.Single(db.AuditLogs.Where(a => a.Action == "approval.import"));
        var history = db.AuditLogs.Single(a => a.Action == ApprovalService.AuditAction);
        Assert.Contains("import", history.DetailJson);
    }

    [Fact]
    public async Task Apply_IsAtomic()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("M");
        var import = Import(t);
        var plan = await import.PlanAsync(Parsed(Row("E1", 2, (1, "M"))), false, "tok", default);
        // A tree step that points at a unit that doesn't exist fails inside the transaction.
        var broken = plan with { Tree = new TreePlan([], [new UserMove("E1", "x", null, "x", null, "NOPE", null)], [], []) };

        await Assert.ThrowsAnyAsync<Exception>(() => import.ApplyAsync(broken, "HC1", default));

        var db = t.Verify();
        Assert.Empty(db.ApproverChains);
        Assert.Null(db.Users.Single(u => u.Nik == "E1").ManagerNik);
    }
}
