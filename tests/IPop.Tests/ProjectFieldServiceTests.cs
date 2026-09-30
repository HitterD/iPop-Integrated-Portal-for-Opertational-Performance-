using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public sealed class ProjectFieldServiceTests
{
    [Fact]
    public async Task Owners_and_managers_manage_definitions_members_edit_values_viewers_only_read()
    {
        using var t = new TestDb();
        var owner = t.AddUser("FIELD_OWNER");
        var manager = t.AddUser("FIELD_MANAGER");
        var member = t.AddUser("FIELD_MEMBER");
        var viewer = t.AddUser("FIELD_VIEWER");
        var outsider = t.AddUser("FIELD_OUT");
        var p = t.AddProject("Fields", owner.Id);
        t.AddProjectMember(p.Id, manager.Id, "manager");
        t.AddProjectMember(p.Id, member.Id);
        t.AddProjectMember(p.Id, viewer.Id, "viewer");
        var task = AddTask(t, p.Id, owner.Id);
        var svc = new ProjectFieldService(t.Verify());

        Assert.Equal("forbidden", (await svc.CreateAsync(member.Id, p.Id, new("X", "Text", null), default)).Error);
        Assert.Equal("forbidden", (await svc.ListAsync(outsider.Id, p.Id, default)).Error);
        var field = (await svc.CreateAsync(manager.Id, p.Id, new("Status note", "Text", null), default)).Data!;
        Assert.Single((await svc.ListAsync(viewer.Id, p.Id, default)).Data!);
        Assert.Equal("forbidden", (await svc.SetTaskValueAsync(viewer.Id, p.Id, task.Id, field.Id, "hello", default)).Error);
        Assert.Equal("hello", (await svc.SetTaskValueAsync(member.Id, p.Id, task.Id, field.Id, "hello", default)).Data!.Value);
        Assert.Equal("hello", Assert.Single((await svc.GetTaskValuesAsync(viewer.Id, p.Id, task.Id, default)).Data!).Value);
        Assert.True((await svc.DeleteAsync(owner.Id, p.Id, field.Id, default)).Success);
        Assert.Empty((await svc.GetTaskValuesAsync(owner.Id, p.Id, task.Id, default)).Data!);
    }

    [Theory]
    [InlineData("Text", "hello", "hello")]
    [InlineData("Number", "1.25", "1.25")]
    [InlineData("Date", "2026-10-04", "2026-10-04")]
    [InlineData("Dropdown", "Ready", "Ready")]
    [InlineData("Link", "https://example.com/path", "https://example.com/path")]
    public async Task Supported_types_round_trip_and_clear(string type, string value, string expected)
    {
        using var t = new TestDb();
        var owner = t.AddUser("TYPE_OWNER");
        var p = t.AddProject("Types", owner.Id);
        var task = AddTask(t, p.Id, owner.Id);
        var svc = new ProjectFieldService(t.Verify());
        var field = (await svc.CreateAsync(owner.Id, p.Id,
            new(type, type, type == "Dropdown" ? ["Ready", "Blocked"] : null), default)).Data!;

        Assert.Equal(expected, (await svc.SetTaskValueAsync(owner.Id, p.Id, task.Id, field.Id, value, default)).Data!.Value);
        Assert.Equal(expected, Assert.Single((await svc.GetTaskValuesAsync(owner.Id, p.Id, task.Id, default)).Data!).Value);
        Assert.True((await svc.SetTaskValueAsync(owner.Id, p.Id, task.Id, field.Id, null, default)).Success);
        Assert.Empty((await svc.GetTaskValuesAsync(owner.Id, p.Id, task.Id, default)).Data!);
    }

    [Fact]
    public async Task Batch_values_respect_project_scope_and_request_limit()
    {
        using var t = new TestDb();
        var owner = t.AddUser("BATCH_OWNER");
        var outsider = t.AddUser("BATCH_OUTSIDER");
        var project = t.AddProject("Batch", owner.Id);
        var task = AddTask(t, project.Id, owner.Id);
        var service = new ProjectFieldService(t.Verify());
        var field = (await service.CreateAsync(owner.Id, project.Id, new("Location", "Text", null), default)).Data!;
        await service.SetTaskValueAsync(owner.Id, project.Id, task.Id, field.Id, "Site A", default);
        Assert.Equal("forbidden", (await service.GetValuesForTasksAsync(outsider.Id, project.Id, [task.Id], default)).Error);
        var values = (await service.GetValuesForTasksAsync(owner.Id, project.Id, [task.Id], default)).Data!;
        Assert.Equal("Site A", Assert.Single(Assert.Single(values).Values).Value);
        Assert.Equal("invalid_task_ids", (await service.GetValuesForTasksAsync(owner.Id, project.Id,
            Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList(), default)).Error);
    }

    [Fact]
    public async Task Person_requires_active_member_of_the_same_project()
    {
        using var t = new TestDb();
        var owner = t.AddUser("PERSON_OWNER");
        var member = t.AddUser("PERSON_MEMBER");
        var other = t.AddUser("PERSON_OTHER");
        var p = t.AddProject("People", owner.Id);
        t.AddProjectMember(p.Id, member.Id);
        var task = AddTask(t, p.Id, owner.Id);
        var svc = new ProjectFieldService(t.Verify());
        var field = (await svc.CreateAsync(owner.Id, p.Id, new("PIC", "Person", null), default)).Data!;
        Assert.Equal("invalid_value", (await svc.SetTaskValueAsync(owner.Id, p.Id, task.Id, field.Id, other.Id.ToString(), default)).Error);
        Assert.Equal(member.Id.ToString(), (await svc.SetTaskValueAsync(owner.Id, p.Id, task.Id, field.Id, member.Id.ToString(), default)).Data!.Value);
    }

    [Fact]
    public async Task Rejects_cross_project_task_or_field_and_invalid_type_values()
    {
        using var t = new TestDb();
        var owner = t.AddUser("BOUND_OWNER");
        var first = t.AddProject("First", owner.Id);
        var second = t.AddProject("Second", owner.Id);
        var task = AddTask(t, first.Id, owner.Id);
        var foreignTask = AddTask(t, second.Id, owner.Id);
        var svc = new ProjectFieldService(t.Verify());
        var dropdown = (await svc.CreateAsync(owner.Id, first.Id, new("State", "Dropdown", ["Ready"]), default)).Data!;
        Assert.Equal("task_not_found", (await svc.SetTaskValueAsync(owner.Id, first.Id, foreignTask.Id, dropdown.Id, "Ready", default)).Error);
        Assert.Equal("field_not_found", (await svc.SetTaskValueAsync(owner.Id, second.Id, foreignTask.Id, dropdown.Id, "Ready", default)).Error);
        Assert.Equal("invalid_value", (await svc.SetTaskValueAsync(owner.Id, first.Id, task.Id, dropdown.Id, "Else", default)).Error);
        Assert.Equal("invalid_options", (await svc.CreateAsync(owner.Id, first.Id, new("Bad", "Dropdown", ["A", "a"]), default)).Error);
        Assert.Equal("invalid_type", (await svc.CreateAsync(owner.Id, first.Id, new("Bad", "File", null), default)).Error);
        var date = (await svc.CreateAsync(owner.Id, first.Id, new("Date", "Date", null), default)).Data!;
        Assert.Equal("invalid_value", (await svc.SetTaskValueAsync(owner.Id, first.Id, task.Id, date.Id, "tomorrow", default)).Error);
        var link = (await svc.CreateAsync(owner.Id, first.Id, new("Link", "Link", null), default)).Data!;
        Assert.Equal("invalid_value", (await svc.SetTaskValueAsync(owner.Id, first.Id, task.Id, link.Id, "javascript:alert(1)", default)).Error);
    }

    [Fact]
    public async Task Caps_fields_at_ten_and_rejects_removing_dropdown_option_in_use()
    {
        using var t = new TestDb();
        var owner = t.AddUser("CAP_OWNER");
        var p = t.AddProject("Limit", owner.Id);
        var task = AddTask(t, p.Id, owner.Id);
        var svc = new ProjectFieldService(t.Verify());
        var field = (await svc.CreateAsync(owner.Id, p.Id, new("State", "Dropdown", ["Ready", "Blocked"]), default)).Data!;
        await svc.SetTaskValueAsync(owner.Id, p.Id, task.Id, field.Id, "Blocked", default);
        Assert.Equal("option_in_use", (await svc.UpdateAsync(owner.Id, p.Id, field.Id, new(null, ["Ready"], null), default)).Error);
        for (var i = 1; i < 10; i++) Assert.True((await svc.CreateAsync(owner.Id, p.Id, new($"Field{i}", "Text", null), default)).Success);
        Assert.Equal("field_limit", (await svc.CreateAsync(owner.Id, p.Id, new("Extra", "Text", null), default)).Error);
        Assert.Equal(10, (await svc.ListAsync(owner.Id, p.Id, default)).Data!.Count);
    }

    private static ProjectTask AddTask(TestDb t, Guid projectId, Guid ownerId)
    {
        var task = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == projectId).Id, CreatedById = ownerId,
            Title = "Task", Status = "todo", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        t.Db.ProjectTasks.Add(task);
        t.Db.SaveChanges();
        return task;
    }
}
