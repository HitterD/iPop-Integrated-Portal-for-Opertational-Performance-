using IPop.Application.Projects;
using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

/// Decision 2026-09-23: anyone can create; members see their projects; admins see all.
public class ProjectVisibilityTests
{
    [Fact]
    public async Task Admin_sees_projects_they_are_not_a_member_of()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var outsider = t.AddUser("OUT");
        var admin = t.AddAdmin("ADM");
        var svc = new ProjectService(t.Verify());
        var created = await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Alpha", null, 2026, owner.Id, null, null, null, null), default);
        var id = created.Project!.Id;

        Assert.Equal(1, (await svc.GetProjectsAsync(admin.Id, null, null, 1, 20, default)).TotalCount);
        Assert.NotNull(await svc.GetProjectAsync(id, admin.Id, default));
        Assert.NotNull(await svc.GetKanbanAsync(id, admin.Id, default));

        Assert.Equal(0, (await svc.GetProjectsAsync(outsider.Id, null, null, 1, 20, default)).TotalCount);
        Assert.Null(await svc.GetProjectAsync(id, outsider.Id, default));
        Assert.Null(await svc.GetKanbanAsync(id, outsider.Id, default));
    }
}
