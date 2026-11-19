using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 1: per-project custom statuses (each mapped to one of the four stages),
/// project health and the saved tab layout.
public sealed class ProjectStatusOptionTests
{
    private static async Task<(TestDb T, ProjectService Projects, ProjectStatusService Statuses, Guid Owner, Guid ProjectId)> SetupAsync(string name = "PS")
    {
        var t = new TestDb();
        var owner = t.AddUser(name + "_O");
        var projects = new ProjectService(t.Verify());
        var p = (await projects.CreateProjectAsync(owner.Id, new CreateProjectRequest(name, null, 2026, owner.Id, null, null, null, null), default)).Project!;
        return (t, projects, new ProjectStatusService(t.Verify()), owner.Id, p.Id);
    }

    private static async Task<ProjectTaskDto> TaskAsync(ProjectService svc, Guid actor, Guid projectId, string title, string status = "todo", Guid? optionId = null) =>
        (await svc.CreateTaskAsync(actor, new CreateTaskRequest(projectId, null, title, null, status, null, null, null, null, null, null, StatusOptionId: optionId), default)).Task!;

    private static List<SaveStatusOptionRequest> Requests(IEnumerable<ProjectStatusOptionDto> options) =>
        options.Select(o => new SaveStatusOptionRequest(o.Id, o.Label, o.Color, o.Stage)).ToList();

    [Fact]
    public async Task Defaults_materialize_once_in_order()
    {
        var (t, _, statuses, owner, p) = await SetupAsync();
        using var _t = t;

        var first = (await statuses.GetAsync(p, owner, default)).Data!;
        var second = (await statuses.GetAsync(p, owner, default)).Data!;

        Assert.Equal(["Not started", "Working on it", "Stuck", "Ready to check", "Done"], first.Select(o => o.Label));
        Assert.Equal(["todo", "doing", "doing", "review", "done"], first.Select(o => o.Stage));
        Assert.Equal(["#c4c4c4", "#fdab3d", "#e2445c", "#579bfc", "#00c875"], first.Select(o => o.Color));
        Assert.Equal(first.Select(o => o.Id), second.Select(o => o.Id));
        Assert.Equal(5, await t.Verify().ProjectStatusOptions.CountAsync(o => o.ProjectId == p));
    }

    [Fact]
    public async Task Materializing_backfills_existing_tasks_with_first_option_of_their_stage()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var doing = await TaskAsync(projects, owner, p, "Legacy", "doing");
        Assert.Null(doing.StatusOptionId);

        var options = (await statuses.GetAsync(p, owner, default)).Data!;
        var working = options.Single(o => o.Label == "Working on it");
        Assert.Equal(1, working.TaskCount);
        Assert.Equal(working.Id, (await projects.GetTaskAsync(doing.Id, owner, default))!.StatusOptionId);
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_save_and_outsider_cannot_read()
    {
        var (t, _, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var viewer = t.AddUser("PS_VIEWER");
        var member = t.AddUser("PS_MEMBER");
        var outsider = t.AddUser("PS_OUT");
        t.AddProjectMember(p, viewer.Id, "viewer");
        t.AddProjectMember(p, member.Id);

        var options = (await statuses.GetAsync(p, viewer.Id, default)).Data!;
        Assert.Equal(5, options.Count);
        Assert.Equal("forbidden", (await statuses.GetAsync(p, outsider.Id, default)).Error);
        Assert.Equal("forbidden", (await statuses.SaveAsync(p, viewer.Id, new(Requests(options)), default)).Error);
        Assert.Equal("forbidden", (await statuses.SaveAsync(p, member.Id, new(Requests(options)), default)).Error);
        Assert.Equal("project_not_found", (await statuses.GetAsync(Guid.NewGuid(), owner, default)).Error);
    }

    [Fact]
    public async Task Save_validates_each_error_code()
    {
        var (t, _, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var current = Requests((await statuses.GetAsync(p, owner, default)).Data!);

        async Task<string?> Save(List<SaveStatusOptionRequest> list) => (await statuses.SaveAsync(p, owner, new(list), default)).Error;
        List<SaveStatusOptionRequest> With(int index, SaveStatusOptionRequest replacement)
        {
            var copy = current.ToList();
            copy[index] = replacement;
            return copy;
        }

        Assert.Equal("invalid_label", await Save(With(1, current[1] with { Label = "  " })));
        Assert.Equal("invalid_label", await Save(With(1, current[1] with { Label = new string('x', 41) })));
        Assert.Equal("invalid_color", await Save(With(1, current[1] with { Color = "orange" })));
        Assert.Equal("invalid_color", await Save(With(1, current[1] with { Color = "#fff" })));
        Assert.Equal("invalid_stage", await Save(With(1, current[1] with { Stage = "blocked" })));
        Assert.Equal("duplicate_label", await Save(With(2, current[2] with { Label = "working ON it" })));
        Assert.Equal("stage_required", await Save(current.Where(o => o.Stage != "todo").ToList()));
        Assert.Equal("stage_required", await Save(current.Where(o => o.Stage != "done").ToList()));
        Assert.Equal("stage_required", await Save([]));
        Assert.Equal("too_many", await Save(current.Concat(Enumerable.Range(0, 16)
            .Select(i => new SaveStatusOptionRequest(null, $"Extra {i}", "#123456", "doing"))).ToList()));
        Assert.Equal("invalid_status_option", await Save(With(1, current[1] with { Id = Guid.NewGuid() })));

        var ok = await statuses.SaveAsync(p, owner, new(current.Append(new SaveStatusOptionRequest(null, "Waiting vendor", "#A25DDC", "doing")).ToList()), default);
        Assert.True(ok.Success);
        Assert.Equal("Waiting vendor", ok.Data![^1].Label);
        Assert.Equal(6, ok.Data.Count);
    }

    [Fact]
    public async Task Save_reorders_and_renames_in_place()
    {
        var (t, _, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var current = Requests((await statuses.GetAsync(p, owner, default)).Data!);
        var reordered = new List<SaveStatusOptionRequest> { current[4], current[0] with { Label = "Backlog" }, current[1], current[2], current[3] };

        var saved = (await statuses.SaveAsync(p, owner, new(reordered), default)).Data!;

        Assert.Equal(["Done", "Backlog", "Working on it", "Stuck", "Ready to check"], saved.Select(o => o.Label));
        Assert.Equal(current[0].Id, saved[1].Id);
        Assert.True(saved.Zip(saved.Skip(1)).All(pair => pair.First.SortOrder < pair.Second.SortOrder));
    }

    [Fact]
    public async Task Stage_change_cascades_to_tasks_and_completed_at()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var options = (await statuses.GetAsync(p, owner, default)).Data!;
        var stuck = options.Single(o => o.Label == "Stuck");
        var task = await TaskAsync(projects, owner, p, "Blocked", optionId: stuck.Id);
        Assert.Equal("doing", task.Status);

        var toDone = Requests(options).Select(o => o.Id == stuck.Id ? o with { Stage = "done" } : o).ToList();
        Assert.True((await statuses.SaveAsync(p, owner, new(toDone), default)).Success);
        var done = (await projects.GetTaskAsync(task.Id, owner, default))!;
        Assert.Equal("done", done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(stuck.Id, done.StatusOptionId);
        Assert.Equal(100, (await projects.GetProjectAsync(p, owner, default))!.Progress);

        var back = toDone.Select(o => o.Id == stuck.Id ? o with { Stage = "doing" } : o).ToList();
        Assert.True((await statuses.SaveAsync(p, owner, new(back), default)).Success);
        var doing = (await projects.GetTaskAsync(task.Id, owner, default))!;
        Assert.Equal("doing", doing.Status);
        Assert.Null(doing.CompletedAt);
    }

    [Fact]
    public async Task Removing_an_option_with_tasks_requires_reassign_and_reassigns()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var options = (await statuses.GetAsync(p, owner, default)).Data!;
        var stuck = options.Single(o => o.Label == "Stuck");
        var done = options.Single(o => o.Label == "Done");
        var task = await TaskAsync(projects, owner, p, "Blocked", optionId: stuck.Id);
        var without = Requests(options).Where(o => o.Id != stuck.Id).ToList();

        Assert.Equal("reassign_required", (await statuses.SaveAsync(p, owner, new(without), default)).Error);
        Assert.Equal("reassign_required", (await statuses.SaveAsync(p, owner,
            new(without, new Dictionary<Guid, Guid> { [stuck.Id] = stuck.Id }), default)).Error);
        Assert.Equal("reassign_required", (await statuses.SaveAsync(p, owner,
            new(without, new Dictionary<Guid, Guid> { [stuck.Id] = Guid.NewGuid() }), default)).Error);

        var saved = await statuses.SaveAsync(p, owner, new(without, new Dictionary<Guid, Guid> { [stuck.Id] = done.Id }), default);
        Assert.True(saved.Success);
        Assert.Equal(4, saved.Data!.Count);
        Assert.Equal(1, saved.Data.Single(o => o.Id == done.Id).TaskCount);
        var moved = (await projects.GetTaskAsync(task.Id, owner, default))!;
        Assert.Equal(done.Id, moved.StatusOptionId);
        Assert.Equal("done", moved.Status);
        Assert.NotNull(moved.CompletedAt);

        // An option without tasks can go without a reassign entry.
        var working = saved.Data.Single(o => o.Label == "Working on it");
        Assert.True((await statuses.SaveAsync(p, owner, new(Requests(saved.Data).Where(o => o.Id != working.Id).ToList()), default)).Success);
    }

    [Fact]
    public async Task Move_with_option_sets_stage_and_legacy_stage_picks_first_option()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var options = (await statuses.GetAsync(p, owner, default)).Data!;
        var working = options.Single(o => o.Label == "Working on it");
        var stuck = options.Single(o => o.Label == "Stuck");
        var review = options.Single(o => o.Stage == "review");
        var task = await TaskAsync(projects, owner, p, "Move me");
        Assert.Equal(options[0].Id, task.StatusOptionId);

        var moved = await projects.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("", null, stuck.Id), default);
        Assert.True(moved.Success);
        Assert.Equal("doing", moved.Task!.Status);
        Assert.Equal(stuck.Id, moved.Task.StatusOptionId);

        // Same stage from a legacy client keeps the specific option it already has.
        var same = await projects.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("doing", null), default);
        Assert.Equal(stuck.Id, same.Task!.StatusOptionId);

        var legacy = await projects.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("review", null), default);
        Assert.Equal("review", legacy.Task!.Status);
        Assert.Equal(review.Id, legacy.Task.StatusOptionId);

        var updated = await projects.UpdateTaskAsync(owner, task.Id, new UpdateTaskRequest(null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, StatusOptionId: working.Id), default);
        Assert.Equal("doing", updated.Task!.Status);
        Assert.Equal(working.Id, updated.Task.StatusOptionId);
    }

    [Fact]
    public async Task Projects_without_option_rows_leave_option_null()
    {
        var (t, projects, _, owner, p) = await SetupAsync();
        using var _t = t;
        var task = await TaskAsync(projects, owner, p, "Plain");
        var moved = await projects.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("doing", null), default);
        Assert.Equal("doing", moved.Task!.Status);
        Assert.Null(moved.Task.StatusOptionId);
        Assert.Equal(0, await t.Verify().ProjectStatusOptions.CountAsync());
    }

    [Fact]
    public async Task Option_from_another_project_is_rejected_everywhere()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var other = (await projects.CreateProjectAsync(owner, new CreateProjectRequest("Other", null, 2026, owner, null, null, null, null), default)).Project!;
        var foreign = (await statuses.GetAsync(other.Id, owner, default)).Data!.Single(o => o.Stage == "done");
        await statuses.GetAsync(p, owner, default);
        var task = await TaskAsync(projects, owner, p, "Mine");

        Assert.Equal("invalid_status_option", (await projects.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("done", null, foreign.Id), default)).Error);
        Assert.Equal("invalid_status_option", (await projects.UpdateTaskAsync(owner, task.Id, new UpdateTaskRequest(null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, StatusOptionId: foreign.Id), default)).Error);
        Assert.Equal("invalid_status_option", (await projects.CreateTaskAsync(owner, new CreateTaskRequest(p, null, "X", null, "todo", null, null, null, null, null, null,
            StatusOptionId: foreign.Id), default)).Error);
        var bulk = await projects.BulkUpdateTasksAsync(owner, p, new BulkTaskRequest([task.Id], "status", foreign.Id.ToString()), default);
        Assert.Equal("invalid_status_option", bulk.Error);
        Assert.Equal("todo", (await projects.GetTaskAsync(task.Id, owner, default))!.Status);
    }

    [Fact]
    public async Task Bulk_status_accepts_option_id_or_stage()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var options = (await statuses.GetAsync(p, owner, default)).Data!;
        var stuck = options.Single(o => o.Label == "Stuck");
        var a = await TaskAsync(projects, owner, p, "A");
        var b = await TaskAsync(projects, owner, p, "B");

        var byOption = await projects.BulkUpdateTasksAsync(owner, p, new BulkTaskRequest([a.Id, b.Id], "status", stuck.Id.ToString()), default);
        Assert.True(byOption.Success);
        Assert.Equal(2, byOption.Updated);
        var after = await projects.GetTasksAsync(p, null, default);
        Assert.All(after, x => { Assert.Equal("doing", x.Status); Assert.Equal(stuck.Id, x.StatusOptionId); });

        var byStage = await projects.BulkUpdateTasksAsync(owner, p, new BulkTaskRequest([a.Id], "status", "done"), default);
        Assert.Equal(1, byStage.Updated);
        var doneTask = (await projects.GetTaskAsync(a.Id, owner, default))!;
        Assert.Equal(options.Single(o => o.Stage == "done").Id, doneTask.StatusOptionId);
        Assert.NotNull(doneTask.CompletedAt);
    }

    [Fact]
    public async Task Health_sets_clears_and_rejects_unknown_values()
    {
        var (t, projects, _, owner, p) = await SetupAsync();
        using var _t = t;
        UpdateProjectRequest Health(string? value) => new(null, null, null, null, null, null, null, null, null, Health: value);

        Assert.Equal("at_risk", (await projects.UpdateProjectAsync(owner, p, Health("at_risk"), default)).Project!.Health);
        Assert.Equal("at_risk", (await projects.UpdateProjectAsync(owner, p, Health(null), default)).Project!.Health);
        Assert.Equal("invalid_health", (await projects.UpdateProjectAsync(owner, p, Health("great"), default)).Error);
        Assert.Null((await projects.UpdateProjectAsync(owner, p, Health(""), default)).Project!.Health);
    }

    [Fact]
    public async Task Tabs_save_validate_and_show_on_project()
    {
        var (t, projects, statuses, owner, p) = await SetupAsync();
        using var _t = t;
        var viewer = t.AddUser("TAB_VIEWER");
        t.AddProjectMember(p, viewer.Id, "viewer");
        Assert.Null((await projects.GetProjectAsync(p, owner, default))!.Tabs);

        var saved = await statuses.SaveTabsAsync(p, owner, new SaveProjectTabsRequest(["overview", "table", "gantt"]), default);
        Assert.Equal(["overview", "table", "gantt"], saved.Data!);
        Assert.Equal(["overview", "table", "gantt"], (await projects.GetProjectAsync(p, owner, default))!.Tabs!);

        Assert.Equal("forbidden", (await statuses.SaveTabsAsync(p, viewer.Id, new(["table"]), default)).Error);
        Assert.Equal("invalid_tabs", (await statuses.SaveTabsAsync(p, owner, new([]), default)).Error);
        Assert.Equal("invalid_tabs", (await statuses.SaveTabsAsync(p, owner, new(["table", "table"]), default)).Error);
        Assert.Equal("invalid_tabs", (await statuses.SaveTabsAsync(p, owner, new(["table", "kanban"]), default)).Error);
        Assert.Equal("invalid_tabs", (await statuses.SaveTabsAsync(p, owner, new(Enumerable.Repeat("table", 17).ToList()), default)).Error);
        var all = new[] { "overview", "table", "board", "gantt", "list", "timeline", "calendar", "workload", "reports", "doc",
            "whiteboard", "form", "files", "activity", "team", "gallery" };
        Assert.True((await statuses.SaveTabsAsync(p, owner, new(all), default)).Success);
    }
}
