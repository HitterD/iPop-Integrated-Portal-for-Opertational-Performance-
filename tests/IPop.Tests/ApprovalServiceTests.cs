using IPop.Application.Approval;
using IPop.Core.Entities;
using IPop.Infrastructure.Approval;
using Xunit;

namespace IPop.Tests;

/// Approver chains: compaction for display, ManagerNik sync, history, bulk edits.
public class ApprovalServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
    private static ApprovalService Svc(TestDb t) => new(t.Db, () => Now);
    private static string?[] Slots(params (int Slot, string Nik)[] set)
    {
        var s = new string?[9];
        foreach (var (slot, nik) in set) s[slot - 1] = nik;
        return s;
    }

    [Fact]
    public void Compact_DropsEmptyAndCollapsesRepeats()
    {
        var steps = ApprovalService.Compact(["A", null, "A", "B", null, null, null, "C", "C"],
            nik => (nik, true, true));
        Assert.Equal(new[] { 1, 4, 8 }, steps.Select(s => s.Slot));
        Assert.Equal(new[] { "A", "B", "C" }, steps.Select(s => s.Nik));
        Assert.Equal(new[] { "Manager", "Asst. Manager 3", "General Manager" }, steps.Select(s => s.Label));
    }

    [Fact]
    public void ManagerNik_SkipsSelf()
    {
        Assert.Equal("Y", ApprovalService.FirstManager("X", ["X", null, "Y", null, null, null, null, null, null]));
        Assert.Null(ApprovalService.FirstManager("X", new string?[9]));
    }

    [Fact]
    public async Task SetSlots_UpdatesManagerNikAndWritesHistory()
    {
        using var t = new TestDb();
        t.AddUser("E100"); var boss = t.AddUser("E200"); boss.Name = "Nico Budiman"; t.Db.SaveChanges();
        t.AddUser("ADMIN");

        var view = await Svc(t).SetSlotsAsync("E100", Slots((1, "E200")), "ADMIN", default);

        Assert.Single(view.Steps);
        var db = t.Verify();
        Assert.Equal("E200", db.Users.Single(u => u.Nik == "E100").ManagerNik);
        Assert.Single(db.AuditLogs.Where(a => a.Action == "approval.chain.update" && a.EntityId == "E100"));
        var history = await new ApprovalService(db, () => Now).HistoryAsync("E100", default);
        Assert.Single(history);
        Assert.Contains("Manager", history[0].Summary);
        Assert.Contains("Nico Budiman", history[0].Summary);
    }

    [Fact]
    public async Task SetSlots_NormalisesNumericNiks_AndRejectsWrongLength()
    {
        using var t = new TestDb();
        t.AddUser("00000100"); t.AddUser("00000200");
        await Svc(t).SetSlotsAsync("00000100", Slots((1, "200")), "ADMIN", default);
        Assert.Equal("00000200", t.Verify().ApproverChains.Single().Slot1Nik);
        await Assert.ThrowsAsync<ArgumentException>(() => Svc(t).SetSlotsAsync("00000100", new string?[3], "ADMIN", default));
    }

    [Fact]
    public async Task Get_FlagsInactiveAndUnknownApprovers()
    {
        using var t = new TestDb();
        t.AddUser("E100"); t.AddUser("E300", active: false);
        t.Db.ApproverChains.Add(Chain("E100", Slots((1, "E300"), (2, "E999"))));
        t.Db.SaveChanges();

        var view = await Svc(t).GetAsync("E100", default);

        Assert.NotNull(view);
        Assert.Contains("inactive_approver", view!.Issues);
        Assert.Contains("unknown_approver", view.Issues);
        Assert.False(view.Steps[0].Active);
        Assert.False(view.Steps[1].Known);
    }

    [Fact]
    public async Task ReplaceEverywhere_RewritesAllSlotsAndManagerNik()
    {
        using var t = new TestDb();
        foreach (var n in new[] { "E1", "E2", "E3", "O", "N" }) t.AddUser(n);
        t.Db.ApproverChains.AddRange(
            Chain("E1", Slots((1, "O"))),
            Chain("E2", Slots((3, "O"), (8, "X"))),
            Chain("E3", Slots((1, "Z"), (9, "O"))));
        t.Db.SaveChanges();

        var replaced = await Svc(t).ReplaceEverywhereAsync("O", "N", "ADMIN", default);

        Assert.Equal(3, replaced);
        var db = t.Verify();
        Assert.DoesNotContain(db.ApproverChains.ToList(), c => c.Slots.Contains("O"));
        Assert.Equal("N", db.Users.Single(u => u.Nik == "E1").ManagerNik);
        Assert.Equal("N", db.Users.Single(u => u.Nik == "E2").ManagerNik);
        Assert.Equal("Z", db.Users.Single(u => u.Nik == "E3").ManagerNik);
    }

    [Fact]
    public async Task BulkSetSlot_SetsOneSlotForMany()
    {
        using var t = new TestDb();
        foreach (var n in new[] { "E1", "E2", "M" }) t.AddUser(n);
        t.Db.ApproverChains.Add(Chain("E1", Slots((8, "G"))));
        t.Db.SaveChanges();

        var n2 = await Svc(t).BulkSetSlotAsync(["E1", "E2"], 1, "M", "ADMIN", default);

        Assert.Equal(2, n2);
        var db = t.Verify();
        Assert.All(db.ApproverChains.ToList(), c => Assert.Equal("M", c.Slot1Nik));
        Assert.Equal("G", db.ApproverChains.Single(c => c.Nik == "E1").Slot8Nik);
        Assert.Equal("M", db.Users.Single(u => u.Nik == "E2").ManagerNik);
    }

    [Fact]
    public async Task List_FiltersByIssueAndIncludesUsersWithoutChain()
    {
        using var t = new TestDb();
        t.AddUser("E1"); t.AddUser("E2"); t.AddUser("M");
        t.Db.ApproverChains.Add(Chain("E1", Slots((1, "M"))));
        t.Db.ApproverChains.Add(Chain("E777", Slots((1, "M"))));   // no account yet
        t.Db.SaveChanges();

        var all = await Svc(t).ListAsync(new ChainQuery(null, null, null, null, null, 1, 50), default);
        Assert.Contains(all.Items, v => v.Nik == "E777");
        Assert.Contains(all.Items, v => v.Nik == "E2" && v.Issues.Contains("not_in_matrix"));

        var missing = await Svc(t).ListAsync(new ChainQuery(null, null, null, null, "not_in_matrix", 1, 50), default);
        Assert.Equal(new[] { "E2", "M" }, missing.Items.Select(v => v.Nik).OrderBy(x => x));

        var search = await Svc(t).ListAsync(new ChainQuery("User E1", null, null, null, null, 1, 50), default);
        Assert.Equal("E1", Assert.Single(search.Items).Nik);
    }

    private static ApproverChain Chain(string nik, string?[] slots)
    {
        var c = new ApproverChain { Nik = nik, UpdatedAt = Now };
        c.SetSlots(slots);
        return c;
    }
}
