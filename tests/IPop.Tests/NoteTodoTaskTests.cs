using IPop.Api.Endpoints;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// "→ Task": a to-do remembers the Projects task made from it, and ticking the to-do completes that task.
public class NoteTodoTaskTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Notes(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner)
        => (await Notes(t).CreateAsync(owner, new CreateNoteRequest(null, "Weekly", null, null, null, null), Ct)).Data!.Id;

    private static async Task SaveAsync(TestDb t, Guid page, params NoteTodoDueDto[] items)
    {
        var owner = await t.Verify().NotePages.Where(p => p.Id == page).Select(p => p.OwnerId).FirstAsync();
        var version = await t.Verify().NotePages.Where(p => p.Id == page).Select(p => p.StateVersion).FirstAsync();
        var r = await Notes(t).SnapshotAsync(owner, page, new NoteSnapshotRequest("AQ==", version, [], "x", 0, 0, items), Ct);
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public async Task The_task_a_to_do_points_at_is_stored_with_it_and_kept_through_text_edits()
    {
        using var t = new TestDb();
        var me = t.AddUser("tt1");
        var page = await PageAsync(t, me.Id);
        var task = Guid.NewGuid();
        await SaveAsync(t, page, new NoteTodoDueDto("k1", me.Id, "2026-12-01", "Order SSD", false, task));
        Assert.Equal(task, t.Verify().NoteTodoDues.Single().TaskId);

        // the text changed (so the key changed); the editor still reports the same task
        await SaveAsync(t, page, new NoteTodoDueDto("k2", me.Id, "2026-12-01", "Order 4 SSD", false, task));
        var row = t.Verify().NoteTodoDues.Single();
        Assert.Equal("k2", row.ItemKey);
        Assert.Equal(task, row.TaskId);

        // a save that no longer reports the task clears it
        await SaveAsync(t, page, new NoteTodoDueDto("k2", me.Id, "2026-12-01", "Order 4 SSD", false));
        Assert.Null(t.Verify().NoteTodoDues.Single().TaskId);
    }

    private static async Task<(Guid ProjectId, Guid TaskId)> TaskAsync(TestDb t, Guid owner, string status = "todo")
    {
        var p = t.AddProject("Alpha", owner);
        var r = await new ProjectService(t.Verify()).CreateTaskAsync(owner, new IPop.Application.Projects.CreateTaskRequest(
            p.Id, null, "Order SSD", null, status, null, null, null, null, null, null), Ct);
        Assert.True(r.Success, r.Error);
        return (p.Id, r.Task!.Id);
    }

    [Fact]
    public async Task A_ticked_to_do_completes_its_open_task_once()
    {
        using var t = new TestDb();
        var me = t.AddUser("tt2");
        var (_, task) = await TaskAsync(t, me.Id);

        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), new ProjectService(t.Verify()), me.Id,
            [new NoteTodoTaskLinkDto(task, true)], NullLogger.Instance, Ct);

        var done = t.Verify().ProjectTasks.Single(x => x.Id == task);
        Assert.Equal("done", done.Status);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task An_unticked_to_do_leaves_the_task_alone_and_unticking_later_does_not_reopen_it()
    {
        using var t = new TestDb();
        var me = t.AddUser("tt3");
        var (_, task) = await TaskAsync(t, me.Id);
        var svc = new ProjectService(t.Verify());

        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), svc, me.Id, [new NoteTodoTaskLinkDto(task, false)], NullLogger.Instance, Ct);
        Assert.Equal("todo", t.Verify().ProjectTasks.Single(x => x.Id == task).Status);

        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), svc, me.Id, [new NoteTodoTaskLinkDto(task, true)], NullLogger.Instance, Ct);
        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), svc, me.Id, [new NoteTodoTaskLinkDto(task, false)], NullLogger.Instance, Ct);
        Assert.Equal("done", t.Verify().ProjectTasks.Single(x => x.Id == task).Status);
    }

    [Fact]
    public async Task A_person_who_cannot_change_the_task_leaves_it_as_it_is()
    {
        using var t = new TestDb();
        var owner = t.AddUser("tt4"); var outsider = t.AddUser("tt5");
        var (_, task) = await TaskAsync(t, owner.Id);

        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), new ProjectService(t.Verify()), outsider.Id,
            [new NoteTodoTaskLinkDto(task, true)], NullLogger.Instance, Ct);

        Assert.Equal("todo", t.Verify().ProjectTasks.Single(x => x.Id == task).Status);
    }

    [Fact]
    public async Task A_task_that_no_longer_exists_is_skipped_without_failing_the_save()
    {
        using var t = new TestDb();
        var me = t.AddUser("tt6");
        await NotesEndpoints.CompleteTasksOfDoneTodosAsync(t.Verify(), new ProjectService(t.Verify()), me.Id,
            [new NoteTodoTaskLinkDto(Guid.NewGuid(), true)], NullLogger.Instance, Ct);
    }
}
