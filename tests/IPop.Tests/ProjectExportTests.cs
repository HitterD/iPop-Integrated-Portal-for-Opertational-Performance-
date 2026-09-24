using IPop.Api.Endpoints;
using IPop.Application.Projects;
using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

public class ProjectExportTests
{
    [Fact]
    public async Task GetProjectExportDataAsync_ReturnsTasksAndMembers()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var svc = new ProjectService(t.Verify());

        var created = await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Project Alpha", null, 2026, owner.Id, null, null, null, null), default);
        var id = created.Project!.Id;

        await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(
            id, null, "Task 1", "Description", "todo", "High", null, null, 10, null, "backend,api"), default);

        var exp = await svc.GetProjectExportDataAsync(id, owner.Id, default);
        Assert.NotNull(exp);
        Assert.Equal("Project Alpha", exp.Project.Name);
        Assert.Single(exp.Tasks);
        Assert.Equal("Task 1", exp.Tasks[0].Title);
        Assert.Single(exp.Members);
    }
}
