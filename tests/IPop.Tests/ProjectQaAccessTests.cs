using IPop.Application.Projects;
using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

public class ProjectQaAccessTests
{
    [Fact]
    public async Task Viewer_can_read_but_cannot_create_move_or_assign_tasks()
    {
        using var db = new TestDb();
        var owner = db.AddUser("QA_OWNER"); var viewer = db.AddUser("QA_VIEW");
        var svc = new ProjectService(db.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("QA", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(viewer.Id, "viewer"), default);
        var task = (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(project.Id, null, "Task", null, "todo", null, null, null, null, null, null), default)).Task!;
        Assert.NotNull(await svc.GetProjectAsync(project.Id, viewer.Id, default));
        Assert.False((await svc.CreateTaskAsync(viewer.Id, new CreateTaskRequest(project.Id, null, "No", null, "todo", null, null, null, null, null, null), default)).Success);
        Assert.False((await svc.MoveTaskAsync(viewer.Id, task.Id, new TaskMoveRequest("done", null), default)).Success);
        Assert.False((await svc.AssignUserAsync(viewer.Id, task.Id, owner.Id, "pic", default)).Success);
        Assert.Equal("todo", (await svc.GetTaskAsync(task.Id, owner.Id, default))!.Status);
    }

    [Fact]
    public async Task Assignee_must_be_a_writable_project_member()
    {
        using var db = new TestDb();
        var owner = db.AddUser("QA_OWNER"); var outsider = db.AddUser("QA_OUT"); var viewer = db.AddUser("QA_VIEW");
        var svc = new ProjectService(db.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("QA", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(viewer.Id, "viewer"), default);
        var task = (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(project.Id, null, "Task", null, "todo", null, null, null, null, null, null), default)).Task!;
        Assert.False((await svc.AssignUserAsync(owner.Id, task.Id, outsider.Id, "pic", default)).Success);
        Assert.False((await svc.AssignUserAsync(owner.Id, task.Id, viewer.Id, "pic", default)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownership_transfer_updates_member_roles(bool byNik)
    {
        using var db = new TestDb();
        var old = db.AddUser("OLD"); var next = db.AddUser("NEXT");
        var svc = new ProjectService(db.Verify());
        var project = (await svc.CreateProjectAsync(old.Id, new CreateProjectRequest("QA", null, 2026, old.Id, null, null, null, null), default)).Project!;
        var request = new UpdateProjectRequest(null, null, null, byNik ? null : next.Id, null, null, null, null, null, byNik ? next.Nik : null);
        Assert.True((await svc.UpdateProjectAsync(old.Id, project.Id, request, default)).Success);
        var members = await svc.GetMembersAsync(project.Id, default);
        Assert.Contains(members, m => m.UserId == old.Id && m.Role == "member");
        Assert.Contains(members, m => m.UserId == next.Id && m.Role == "owner");
        var room = db.Db.ProjectRooms.Single(r => r.ProjectId == project.Id);
        Assert.Contains(db.Db.ChatRoomMembers, m => m.RoomId == room.RoomId && m.UserId == next.Id && m.Role == "owner");
        Assert.Contains(db.Db.ChatRoomMembers, m => m.RoomId == room.RoomId && m.UserId == old.Id && m.Role == "member");
        Assert.False((await svc.DeleteProjectAsync(old.Id, project.Id, default)).Success);
        Assert.True((await svc.DeleteProjectAsync(next.Id, project.Id, default)).Success);
    }

    [Fact]
    public async Task Viewer_cannot_create_milestone_or_stop_preexisting_timer()
    {
        using var db = new TestDb();
        var owner = db.AddUser("QA_OWNER"); var viewer = db.AddUser("QA_VIEW");
        var svc = new ProjectService(db.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("QA", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(viewer.Id, "member"), default);
        var task = (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(project.Id, null, "Task", null, "todo", null, null, null, null, null, null), default)).Task!;
        var time = new ProjectTimeService(db.Verify());
        await time.StartAsync(viewer.Id, task.Id, default, DateTime.UtcNow.AddMinutes(-5));
        var membership = db.Db.ProjectMembers.Single(m => m.ProjectId == project.Id && m.UserId == viewer.Id);
        membership.Role = "viewer";
        await db.Db.SaveChangesAsync();
        var ms = await new MilestoneService(db.Verify()).CreateAsync(viewer.Id, project.Id,
            new CreateMilestoneRequest("No", DateTime.UtcNow.AddDays(2), []), DateTime.UtcNow, default);
        Assert.False(ms.Success);
        Assert.False((await time.StopAsync(viewer.Id, default)).Success);
    }

    [Fact]
    public async Task Explicit_clear_removes_dates_estimate_but_omitted_values_preserve_them()
    {
        using var db = new TestDb(); var owner = db.AddUser("QA_OWNER");
        var svc = new ProjectService(db.Verify());
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("QA", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var day = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Utc);
        var t = (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(p.Id, null, "Task", null, "todo", null, day, day.AddDays(2), 4, null, null), default)).Task!;
        var omitted = new UpdateTaskRequest("Renamed", null, null, null, null, null, null, null, null, null, null, null, null, null);
        var kept = (await svc.UpdateTaskAsync(owner.Id, t.Id, omitted, default)).Task!;
        Assert.Equal(day, kept.StartDate); Assert.Equal(4m, kept.EstimatedHours);
        var cleared = (await svc.UpdateTaskAsync(owner.Id, t.Id, omitted with { ClearStartDate = true, ClearDueDate = true, ClearEstimatedHours = true }, default)).Task!;
        Assert.Null(cleared.StartDate); Assert.Null(cleared.DueDate); Assert.Null(cleared.EstimatedHours);
    }

    [Fact]
    public async Task Removed_member_cannot_mutate_historical_private_view()
    {
        using var db = new TestDb(); var owner = db.AddUser("QA_OWNER"); var member = db.AddUser("QA_MEMBER");
        var svc = new ProjectService(db.Verify());
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("QA", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, p.Id, new AddProjectMemberRequest(member.Id, "member"), default);
        var view = (await svc.CreateSavedViewAsync(member.Id, p.Id, new SaveProjectViewRequest("Mine", "{}"), default)).View!;
        await svc.RemoveMemberAsync(owner.Id, p.Id, member.Id, default);
        Assert.False((await svc.UpdateSavedViewAsync(member.Id, view.Id, new SaveProjectViewRequest("No", "{}"), default)).Success);
        Assert.False((await svc.DeleteSavedViewAsync(member.Id, view.Id, default)).Success);
    }
}
