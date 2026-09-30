using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

public sealed class PortfolioServiceTests
{
    private static readonly DateTime Today = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private static ProjectTask AddTask(TestDb db, Project project, string name, DateTime? due, string status = "todo", decimal? hours = null, Guid? assignee = null)
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ProjectId = project.Id,
            GroupId = db.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = name,
            Status = status, DueDate = due, EstimatedHours = hours, CreatedById = project.OwnerId,
            CreatedAt = Today, UpdatedAt = Today };
        db.Db.ProjectTasks.Add(task);
        if (assignee.HasValue)
            db.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = assignee.Value, AssignedAt = Today });
        db.Db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Member_sees_only_active_member_projects_and_scoped_drilldowns()
    {
        using var db = new TestDb();
        var member = db.AddUser("MEM");
        var other = db.AddUser("OTH");
        var visible = db.AddProject("Visible", member.Id);
        var hidden = db.AddProject("Hidden", other.Id);
        var archived = db.AddProject("Archived", member.Id);
        archived.IsActive = false;
        db.Db.SaveChanges();
        AddTask(db, visible, "Visible late", Today.AddDays(-1), assignee: member.Id);
        AddTask(db, hidden, "Hidden late", Today.AddDays(-2), assignee: member.Id);
        AddTask(db, archived, "Archived late", Today.AddDays(-3), assignee: member.Id);
        var svc = new PortfolioService(db.Verify());
        var result = await svc.GetPortfolioAsync(member.Id, default, Today);
        Assert.Equal("member", result.Scope);
        Assert.False(result.IsSuperAdmin);
        Assert.Equal(1, result.ActiveProjects);
        Assert.Equal(1, result.OverdueTasks);
        Assert.Equal(visible.Id, Assert.Single(result.Projects.Items).ProjectId);
        var drilldown = await svc.GetTasksAsync(member.Id, "overdue", null, null, 1, 20, default, Today);
        Assert.Equal(1, drilldown.TotalCount);
        Assert.Equal(visible.Id, Assert.Single(drilldown.Items).ProjectId);
        Assert.Empty((await svc.GetTasksAsync(member.Id, "overdue", hidden.Id, null, 1, 20, default, Today)).Items);
    }

    [Theory]
    [InlineData("Admin", false)]
    [InlineData("HR", false)]
    [InlineData("Super Admin", true)]
    public async Task Elevated_roles_access_every_active_project_but_super_admin_flag_requires_real_role(string role, bool super)
    {
        using var db = new TestDb();
        var owner = db.AddUser("OWN");
        var admin = db.AddUser("ADM");
        db.AssignRole(admin.Id, role);
        var project = db.AddProject("All", owner.Id);
        AddTask(db, project, "Open", Today.AddDays(-1));
        var result = await new PortfolioService(db.Verify()).GetPortfolioAsync(admin.Id, default, Today);
        Assert.Equal("all", result.Scope);
        Assert.Equal(super, result.IsSuperAdmin);
        Assert.Equal(1, result.OverdueTasks);
    }

    [Fact]
    public async Task Counts_due_week_overdue_workload_health_and_pagination_without_done_tasks()
    {
        using var db = new TestDb();
        var owner = db.AddUser("OWN");
        var teammate = db.AddUser("TEAM");
        var project = db.AddProject("Deadlines", owner.Id);
        project.Deadline = Today.AddDays(5);
        db.Db.SaveChanges();
        AddTask(db, project, "Yesterday", Today.AddDays(-1), assignee: teammate.Id);
        AddTask(db, project, "Today", Today, hours: 5, assignee: owner.Id);
        AddTask(db, project, "Tomorrow", Today.AddDays(1), hours: 4, assignee: owner.Id);
        AddTask(db, project, "Day seven excluded", Today.AddDays(7), hours: 99, assignee: owner.Id);
        AddTask(db, project, "Done", Today.AddDays(-2), "done", assignee: owner.Id);
        var svc = new PortfolioService(db.Verify());
        var result = await svc.GetPortfolioAsync(owner.Id, default, Today);
        Assert.Equal(2, result.MyDueWeek);
        Assert.Equal(1, result.OverdueTasks);
        Assert.Equal("at_risk", Assert.Single(result.Projects.Items).Health);
        Assert.Equal(1, Assert.Single(result.OverdueByPerson).OverdueCount);
        var workload = Assert.Single(result.WorkloadByPerson, p => p.UserId == owner.Id);
        Assert.Equal(9, workload.EstimatedHours);
        Assert.False(workload.OverCapacity);
        Assert.Equal(8, workload.CapacityTasks);
        Assert.Equal(project.Id, Assert.Single(result.Deadlines).ProjectId);
        var page = await svc.GetTasksAsync(owner.Id, "due_week", null, null, 2, 1, default, Today);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal("Tomorrow", Assert.Single(page.Items).Title);
        Assert.Equal(1, (await svc.GetTasksAsync(owner.Id, "overdue", null, teammate.Id, 1, 10, default, Today)).TotalCount);
    }

    [Fact]
    public async Task Project_cards_are_paginated_and_aggregates_exclude_subtasks()
    {
        using var db = new TestDb();
        var owner = db.AddUser("OWNER");
        var first = db.AddProject("Alpha", owner.Id);
        db.AddProject("Beta", owner.Id);
        db.AddProject("Gamma", owner.Id);
        var parent = AddTask(db, first, "Parent", Today.AddDays(1), assignee: owner.Id);
        var child = AddTask(db, first, "Child", Today.AddDays(-1), assignee: owner.Id);
        child.ParentTaskId = parent.Id;
        db.Db.SaveChanges();
        var svc = new PortfolioService(db.Verify());
        var firstPage = await svc.GetPortfolioAsync(owner.Id, default, Today, 1, 1);
        Assert.Equal(3, firstPage.ActiveProjects);
        Assert.Equal(3, firstPage.Projects.TotalCount);
        Assert.Equal(1, firstPage.Projects.PageSize);
        Assert.Equal(first.Id, Assert.Single(firstPage.Projects.Items).ProjectId);
        Assert.Equal(1, firstPage.Projects.Items[0].TaskCount);
        Assert.Equal(0, firstPage.Projects.Items[0].OverdueCount);
        Assert.Equal(0, firstPage.OverdueTasks);
        Assert.Equal(1, firstPage.MyDueWeek);
        Assert.Equal(0, (await svc.GetTasksAsync(owner.Id, "overdue", null, null, 1, 10, default, Today)).TotalCount);
        var lastPage = await svc.GetPortfolioAsync(owner.Id, default, Today, 3, 1);
        Assert.Equal("Gamma", Assert.Single(lastPage.Projects.Items).Name);
    }

    [Fact]
    public async Task Capacity_is_eight_tasks_not_eight_estimated_hours()
    {
        using var db = new TestDb();
        var owner = db.AddUser("OWNER");
        var project = db.AddProject("Nine", owner.Id);
        for (var i = 0; i < 9; i++)
            AddTask(db, project, $"Task {i}", Today.AddDays(1), hours: 0.5m, assignee: owner.Id);
        var person = Assert.Single((await new PortfolioService(db.Verify()).GetPortfolioAsync(owner.Id, default, Today)).WorkloadByPerson);
        Assert.Equal(9, person.OpenTaskCount);
        Assert.Equal(4.5m, person.EstimatedHours);
        Assert.Equal(8, person.CapacityTasks);
        Assert.True(person.OverCapacity);
    }
}
