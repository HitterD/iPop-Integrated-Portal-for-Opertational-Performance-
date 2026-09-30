using IPop.Application.Projects;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public sealed class ProjectGroupServiceTests
{
    [Fact]
    public async Task New_project_has_default_group_and_tasks_inherit_parent_group()
    {
        using var test = new TestDb();
        var owner = test.AddUser("GROUP_OWNER");
        var service = new ProjectService(test.Verify());
        var project = (await service.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Grouped", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var initial = Assert.Single((await service.GetGroupsAsync(project.Id, owner.Id, default))!);
        Assert.Equal("Tasks", initial.Name);
        var second = (await service.CreateGroupAsync(owner.Id, project.Id, new("Phase two"), default)).Group!;
        var parent = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Parent", null, "todo", null, null, null, null, null, null, second.Id), default)).Task!;
        var child = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, parent.Id, "Child", null, "todo", null, null, null, null, null, null), default)).Task!;

        Assert.Equal(second.Id, parent.GroupId);
        Assert.Equal(second.Id, child.GroupId);
        Assert.Equal(1, (await service.GetGroupsAsync(project.Id, owner.Id, default))!.Single(g => g.Id == second.Id).TaskCount);
        Assert.Equal("invalid_group", (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, parent.Id, "Wrong child", null, "todo", null, null, null, null, null, null, initial.Id), default)).Error);
    }

    [Fact]
    public async Task Group_management_checks_roles_and_project_boundaries()
    {
        using var test = new TestDb();
        var owner = test.AddUser("GROUP_OWNER_2");
        var member = test.AddUser("GROUP_MEMBER");
        var outsider = test.AddUser("GROUP_OUTSIDER");
        var first = test.AddProject("First", owner.Id);
        var second = test.AddProject("Second", owner.Id);
        test.AddProjectMember(first.Id, member.Id);
        var service = new ProjectService(test.Verify());

        Assert.Null(await service.GetGroupsAsync(first.Id, outsider.Id, default));
        Assert.Equal("forbidden", (await service.CreateGroupAsync(member.Id, first.Id, new("No"), default)).Error);
        var group = (await service.CreateGroupAsync(owner.Id, first.Id, new("New", "#0A84FF"), default)).Group!;
        Assert.Equal("#0A84FF", group.Color);
        Assert.Equal("forbidden", (await service.UpdateGroupAsync(member.Id, group.Id, new("Denied"), default)).Error);
        var task = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(second.Id, null, "Other", null, "todo", null, null, null, null, null, null), default)).Task!;
        Assert.Equal("invalid_group", (await service.MoveTaskToGroupAsync(owner.Id, task.Id, new(group.Id), default)).Error);
        Assert.Equal("forbidden", (await service.MoveTaskToGroupAsync(outsider.Id, task.Id, new(group.Id), default)).Error);
    }

    [Fact]
    public async Task Move_parent_moves_children_and_delete_moves_tasks_to_first_remaining_group()
    {
        using var test = new TestDb();
        var owner = test.AddUser("GROUP_OWNER_3");
        var project = test.AddProject("Move", owner.Id);
        var service = new ProjectService(test.Verify());
        var first = Assert.Single((await service.GetGroupsAsync(project.Id, owner.Id, default))!);
        var second = (await service.CreateGroupAsync(owner.Id, project.Id, new("Second"), default)).Group!;
        var parent = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Parent", null, "todo", null, null, null, null, null, null), default)).Task!;
        var child = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, parent.Id, "Child", null, "todo", null, null, null, null, null, null), default)).Task!;
        Assert.Equal("move_parent_instead", (await service.MoveTaskToGroupAsync(owner.Id, child.Id, new(second.Id), default)).Error);
        Assert.True((await service.MoveTaskToGroupAsync(owner.Id, parent.Id, new(second.Id), default)).Success);
        Assert.Equal(second.Id, (await service.GetTaskAsync(child.Id, owner.Id, default))!.GroupId);
        var delete = await service.DeleteGroupAsync(owner.Id, second.Id, default);
        Assert.True(delete.Success);
        Assert.Equal(first.Id, delete.Deleted!.MovedToGroupId);
        Assert.Equal(2, delete.Deleted.TaskIds.Count);
        using (var verify = test.Verify())
        {
            Assert.Equal(first.Id, await verify.ProjectTasks.Where(t => t.Id == child.Id).Select(t => t.GroupId).SingleAsync());
            Assert.Equal(first.Id, await verify.ProjectTasks.Where(t => t.Id == parent.Id).Select(t => t.GroupId).SingleAsync());
        }
        Assert.Equal("last_group", (await service.DeleteGroupAsync(owner.Id, first.Id, default)).Error);
    }

    [Fact]
    public async Task Groups_accept_the_clients_unspecified_local_date()
    {
        using var test = new TestDb();
        var owner = test.AddUser("GROUP_OWNER_5");
        var project = test.AddProject("Local date", owner.Id);
        var service = new ProjectService(test.Verify());
        var unspecified = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Unspecified);
        var groups = await service.GetGroupsAsync(project.Id, owner.Id, default, unspecified);
        Assert.Single(groups!);
    }

    [Fact]
    public async Task Restoring_a_deleted_group_moves_back_only_untouched_tasks()
    {
        using var test = new TestDb();
        var owner = test.AddUser("GROUP_OWNER_4");
        var other = test.AddUser("GROUP_OUTSIDER_4");
        var project = test.AddProject("Restore", owner.Id);
        var service = new ProjectService(test.Verify());
        var first = Assert.Single((await service.GetGroupsAsync(project.Id, owner.Id, default))!);
        var second = (await service.CreateGroupAsync(owner.Id, project.Id, new("Second", "#0A84FF"), default)).Group!;
        var third = (await service.CreateGroupAsync(owner.Id, project.Id, new("Third"), default)).Group!;
        var stays = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Stays", null, "todo", null, null, null, null, null, null, second.Id), default)).Task!;
        var edited = (await service.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Edited", null, "todo", null, null, null, null, null, null, second.Id), default)).Task!;

        var deleted = (await service.DeleteGroupAsync(owner.Id, second.Id, default)).Deleted!;
        // Someone moves one of the tasks elsewhere before Undo is clicked.
        Assert.True((await service.MoveTaskToGroupAsync(owner.Id, edited.Id, new(third.Id), default)).Success);

        Assert.Equal("forbidden", (await service.RestoreGroupAsync(other.Id, project.Id, deleted, default)).Error);
        var restored = await service.RestoreGroupAsync(owner.Id, project.Id, deleted, default);
        Assert.True(restored.Success);
        Assert.Equal("Second", restored.Group!.Name);
        Assert.Equal("group_exists", (await service.RestoreGroupAsync(owner.Id, project.Id, deleted, default)).Error);

        using var verify = test.Verify();
        Assert.Equal(second.Id, await verify.ProjectTasks.Where(t => t.Id == stays.Id).Select(t => t.GroupId).SingleAsync());
        Assert.Equal(third.Id, await verify.ProjectTasks.Where(t => t.Id == edited.Id).Select(t => t.GroupId).SingleAsync());
        Assert.NotEqual(first.Id, second.Id);
    }
}
