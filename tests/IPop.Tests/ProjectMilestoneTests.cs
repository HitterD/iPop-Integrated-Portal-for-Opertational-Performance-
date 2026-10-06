using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Milestones that group several tasks: link set, auto-achieve, manual mark + undo, status
/// mapping, membership, and the old single TaskId kept in sync.
public sealed class ProjectMilestoneTests
{
    private static readonly DateTime Today = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Ctx(TestDb T, ProjectService Svc, Guid Owner, Guid Member, Guid Outsider, Guid ProjectId) : IDisposable
    {
        public void Dispose() => T.Dispose();
        public MilestoneService Ms() => new(T.Verify());
    }

    private static async Task<Ctx> SetupAsync()
    {
        var t = new TestDb();
        var owner = t.AddUser("MS_O");
        var member = t.AddUser("MS_M");
        var outsider = t.AddUser("MS_X");
        var svc = new ProjectService(t.Verify());
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("Milestones", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, p.Id, new AddProjectMemberRequest(member.Id, "member"), default);
        return new Ctx(t, svc, owner.Id, member.Id, outsider.Id, p.Id);
    }

    private static async Task<Guid> TaskAsync(Ctx c, string title, string status = "todo") =>
        (await c.Svc.CreateTaskAsync(c.Owner, new CreateTaskRequest(c.ProjectId, null, title, null, status, null, null, null, null, null, null), default)).Task!.Id;

    private static UpdateTaskRequest Status(string status) =>
        new(null, null, status, null, null, null, null, null, null, null, null, null, null, null);

    [Theory]
    [InlineData(-1, null, 0, 0, "overdue")]
    [InlineData(0, null, 0, 0, "due_soon")]
    [InlineData(7, null, 3, 1, "due_soon")]
    [InlineData(8, null, 3, 1, "planned")]
    [InlineData(-5, null, 2, 2, "achieved")]   // every linked task done → achieved even if late
    [InlineData(30, "2026-10-01", 0, 0, "achieved")]
    [InlineData(-3, null, 0, 0, "overdue")]    // no linked tasks never auto-achieves
    public void Status_mapping(int dueInDays, string? achievedOn, int linked, int done, string expected)
    {
        DateTime? achieved = achievedOn is null ? null : DateTime.Parse(achievedOn);
        Assert.Equal(expected, MilestoneStatus.Of(Today.AddDays(dueInDays), achieved, linked, done, Today));
    }

    [Fact]
    public async Task Create_links_many_tasks_and_reports_progress()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A", "done");
        var b = await TaskAsync(c, "B");
        var d = await TaskAsync(c, "C");

        var r = await c.Ms().CreateAsync(c.Member, c.ProjectId, new CreateMilestoneRequest(" UAT ready ", Today.AddDays(20), [a, b, d, b]), Today, default);

        Assert.True(r.Success, r.Error);
        Assert.Equal("UAT ready", r.Value!.Title);
        Assert.Equal(3, r.Value.LinkedCount);
        Assert.Equal(1, r.Value.DoneCount);
        Assert.Equal("planned", r.Value.Status);
        Assert.Equal(new[] { a, b, d }.OrderBy(x => x), r.Value.TaskIds.OrderBy(x => x));
        // Back-compat: the single TaskId column still names one linked task.
        using var v = c.T.Verify();
        Assert.Contains((await v.ProjectMilestones.SingleAsync()).TaskId!.Value, new[] { a, b, d });
    }

    [Fact]
    public async Task Auto_achieves_when_every_linked_task_is_done()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B");
        var m = (await c.Ms().CreateAsync(c.Owner, c.ProjectId, new CreateMilestoneRequest("Go-live", Today.AddDays(-2), [a, b]), Today, default)).Value!;
        Assert.Equal("overdue", m.Status);

        await c.Svc.UpdateTaskAsync(c.Owner, a, Status("done"), default);
        await c.Svc.UpdateTaskAsync(c.Owner, b, Status("done"), default);

        var row = (await c.Ms().ListAsync(c.Member, c.ProjectId, Today, default)).Value!.Single();
        Assert.Equal("achieved", row.Status);
        Assert.Equal(2, row.DoneCount);
        Assert.False(row.AchievedManually);
        Assert.NotNull(row.AchievedAt);
    }

    [Fact]
    public async Task Mark_achieved_then_undo()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var m = (await c.Ms().CreateAsync(c.Owner, c.ProjectId, new CreateMilestoneRequest("Training", Today.AddDays(3), [a]), Today, default)).Value!;
        Assert.Equal("due_soon", m.Status);

        var done = await c.Ms().SetAchievedAsync(c.Member, c.ProjectId, m.Id, true, Today, default);
        Assert.Equal("achieved", done.Value!.Status);
        Assert.True(done.Value.AchievedManually);
        var first = done.Value.AchievedAt;
        // Marking again keeps the original time.
        Assert.Equal(first, (await c.Ms().SetAchievedAsync(c.Member, c.ProjectId, m.Id, true, Today, default)).Value!.AchievedAt);

        var undone = await c.Ms().SetAchievedAsync(c.Member, c.ProjectId, m.Id, false, Today, default);
        Assert.Equal("due_soon", undone.Value!.Status);
        Assert.Null(undone.Value.AchievedAt);
    }

    [Fact]
    public async Task Update_replaces_the_link_set_and_fields()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B");
        var d = await TaskAsync(c, "C", "done");
        var m = (await c.Ms().CreateAsync(c.Owner, c.ProjectId, new CreateMilestoneRequest("X", Today.AddDays(30), [a, b]), Today, default)).Value!;

        var r = await c.Ms().UpdateAsync(c.Owner, c.ProjectId, m.Id, new UpdateMilestoneRequest(Title: "Y", DueDate: Today.AddDays(1), TaskIds: [b, d]), Today, default);
        Assert.True(r.Success, r.Error);
        Assert.Equal("Y", r.Value!.Title);
        Assert.Equal(new[] { b, d }.OrderBy(x => x), r.Value.TaskIds.OrderBy(x => x));
        Assert.Equal(1, r.Value.DoneCount);
        Assert.Equal("due_soon", r.Value.Status);

        var cleared = await c.Ms().UpdateAsync(c.Owner, c.ProjectId, m.Id, new UpdateMilestoneRequest(TaskIds: []), Today, default);
        Assert.Equal(0, cleared.Value!.LinkedCount);
        using var v = c.T.Verify();
        Assert.Null((await v.ProjectMilestones.SingleAsync()).TaskId);
        Assert.Equal(0, await v.ProjectMilestoneTasks.CountAsync());
    }

    [Fact]
    public async Task Rejects_outsiders_foreign_tasks_and_blank_titles()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var other = (await c.Svc.CreateProjectAsync(c.Owner, new CreateProjectRequest("Other", null, 2026, c.Owner, null, null, null, null), default)).Project!;
        var foreign = (await c.Svc.CreateTaskAsync(c.Owner, new CreateTaskRequest(other.Id, null, "F", null, "todo", null, null, null, null, null, null), default)).Task!.Id;

        Assert.Equal("forbidden", (await c.Ms().ListAsync(c.Outsider, c.ProjectId, Today, default)).Error);
        Assert.Equal("forbidden", (await c.Ms().CreateAsync(c.Outsider, c.ProjectId, new CreateMilestoneRequest("M", Today), Today, default)).Error);
        Assert.Equal("invalid_title", (await c.Ms().CreateAsync(c.Member, c.ProjectId, new CreateMilestoneRequest("  ", Today), Today, default)).Error);
        Assert.Equal("task_not_found", (await c.Ms().CreateAsync(c.Member, c.ProjectId, new CreateMilestoneRequest("M", Today, [a, foreign]), Today, default)).Error);
        Assert.Equal("project_not_found", (await c.Ms().ListAsync(c.Member, Guid.NewGuid(), Today, default)).Error);

        var m = (await c.Ms().CreateAsync(c.Member, c.ProjectId, new CreateMilestoneRequest("M", Today, [a]), Today, default)).Value!;
        Assert.Equal("forbidden", (await c.Ms().SetAchievedAsync(c.Outsider, c.ProjectId, m.Id, true, Today, default)).Error);
        Assert.Equal("forbidden", (await c.Ms().DeleteAsync(c.Outsider, c.ProjectId, m.Id, default)).Error);
        Assert.Equal("milestone_not_found", (await c.Ms().DeleteAsync(c.Member, c.ProjectId, Guid.NewGuid(), default)).Error);
    }

    [Fact]
    public async Task Delete_removes_links_and_deleted_tasks_drop_out_of_progress()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B", "done");
        var m = (await c.Ms().CreateAsync(c.Owner, c.ProjectId, new CreateMilestoneRequest("M", Today.AddDays(30), [a, b]), Today, default)).Value!;

        await c.Svc.DeleteTaskAsync(c.Owner, a, default);
        var row = (await c.Ms().ListAsync(c.Owner, c.ProjectId, Today, default)).Value!.Single();
        Assert.Equal(1, row.LinkedCount);
        Assert.Equal("achieved", row.Status);

        Assert.True((await c.Ms().DeleteAsync(c.Owner, c.ProjectId, m.Id, default)).Success);
        using var v = c.T.Verify();
        Assert.Equal(0, await v.ProjectMilestones.CountAsync());
        Assert.Equal(0, await v.ProjectMilestoneTasks.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Legacy_single_task_requests_still_work()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B");
        var m = (await c.Ms().CreateAsync(c.Owner, c.ProjectId, new CreateMilestoneRequest("M", Today, TaskId: a), Today, default)).Value!;
        Assert.Equal([a], m.TaskIds);

        var moved = (await c.Ms().UpdateAsync(c.Owner, c.ProjectId, m.Id, new UpdateMilestoneRequest(TaskId: b), Today, default)).Value!;
        Assert.Equal([b], moved.TaskIds);
        var cleared = (await c.Ms().UpdateAsync(c.Owner, c.ProjectId, m.Id, new UpdateMilestoneRequest(ClearTask: true), Today, default)).Value!;
        Assert.Empty(cleared.TaskIds);
    }
}
