using System.Text;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Regressions found by the Projects v2 audit (2026-11-18).
public sealed class ProjectsV2AuditFixTests
{
    private static string Mspdi(string tasks) =>
        $"<?xml version=\"1.0\"?><Project xmlns=\"http://schemas.microsoft.com/project\"><Tasks>{tasks}</Tasks></Project>";

    [Fact]
    public async Task Import_ignores_absurd_dates_and_durations_so_the_gantt_still_loads()
    {
        using var f = new GanttFixture();
        var tasks =
            "<Task><UID>1</UID><Name>Far future</Name><OutlineLevel>1</OutlineLevel><Start>9999-12-30T08:00:00</Start><Finish>9999-12-31T17:00:00</Finish></Task>" +
            "<Task><UID>2</UID><Name>Year one</Name><OutlineLevel>1</OutlineLevel><Start>0001-01-01T08:00:00</Start></Task>" +
            "<Task><UID>3</UID><Name>Huge duration</Name><OutlineLevel>1</OutlineLevel><Start>2026-11-02T08:00:00</Start><Duration>PT99999999H0M0S</Duration></Task>";
        var r = await f.G().ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes(Mspdi(tasks)), "a.xml", false, default);
        Assert.True(r.Success, r.Error);

        using (var v = f.T.Verify())
        {
            var far = await v.ProjectTasks.SingleAsync(t => t.Title == "Far future");
            Assert.Null(far.StartDate); Assert.Null(far.DueDate);
            var one = await v.ProjectTasks.SingleAsync(t => t.Title == "Year one");
            Assert.Null(one.StartDate);
            var huge = await v.ProjectTasks.SingleAsync(t => t.Title == "Huge duration");
            Assert.True(huge.DueDate < new DateTime(2040, 1, 1));
        }
        var gantt = await f.G().GetGanttV2Async(f.Owner, f.ProjectId, null, null, default);
        Assert.True(gantt.Success, gantt.Error);
    }

    [Fact]
    public async Task Reschedule_and_revert_reject_dates_outside_the_supported_window()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        var g = f.G();
        var far = new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("invalid_range", (await g.PreviewRescheduleAsync(f.Owner, f.ProjectId, new RescheduleRequest(a, far), default)).Error);
        Assert.Equal("invalid_range", (await g.ApplyRescheduleAsync(f.Owner, f.ProjectId, new RescheduleRequest(a, GanttFixture.D(2), "single", far), default)).Error);
        Assert.Equal("invalid_range", (await g.RevertRescheduleAsync(f.Owner, f.ProjectId,
            new RescheduleRevertRequest([new RevertChangeDto(a, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), GanttFixture.D(3))]), default)).Error);
        Assert.Equal("invalid_range", (await g.RevertRescheduleAsync(f.Owner, f.ProjectId,
            new RescheduleRevertRequest([new RevertChangeDto(a, GanttFixture.D(9), GanttFixture.D(3))]), default)).Error);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(a));
    }

    [Fact]
    public async Task Constraint_date_outside_the_window_is_rejected()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        var r = await f.Svc.UpdateTaskAsync(f.Owner, a, new UpdateTaskRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            ConstraintType: "snet", ConstraintDate: new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc)), default);
        Assert.Equal("invalid_constraint", r.Error);
    }

    [Fact]
    public void Work_calendar_does_not_underflow_on_a_year_one_date()
    {
        var min = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var a = GanttScheduler.Analyze([new SchedTask(Guid.NewGuid(), min, min.AddDays(2))], []);
        Assert.Single(a.Floats);
    }

    [Fact]
    public async Task Gantt_writes_are_refused_on_a_deleted_project()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        using (var db = f.T.Verify())
        {
            var p = await db.Projects.SingleAsync(x => x.Id == f.ProjectId);
            p.IsActive = false;
            await db.SaveChangesAsync();
        }
        var g = f.G();
        Assert.Equal("project_not_found", (await g.ApplyRescheduleAsync(f.Owner, f.ProjectId, new RescheduleRequest(a, GanttFixture.D(4)), default)).Error);
        Assert.Equal("project_not_found", (await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default)).Error);
        Assert.Equal("project_not_found", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId,
            Encoding.UTF8.GetBytes(Mspdi("<Task><UID>1</UID><Name>x</Name><OutlineLevel>1</OutlineLevel></Task>")), "a.xml", false, default)).Error);
        Assert.Equal("project_not_found", (await g.GetGanttV2Async(f.Owner, f.ProjectId, null, null, default)).Error);
        Assert.Equal("project_not_found", (await g.ExportMsProjectAsync(f.Owner, f.ProjectId, default)).Error);
    }

    [Fact]
    public async Task Due_tomorrow_reminder_matches_a_due_date_that_carries_a_time()
    {
        using var t = new TestDb();
        var owner = t.AddUser("AF_O"); var member = t.AddUser("AF_M");
        var gw = new RecordingGateway();
        var svc = new ProjectService(t.Verify(), gw);
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("AF", null, 2026, owner.Id, null, null, null, null), default)).Project!.Id;
        t.AddProjectMember(p, member.Id);
        // The API stores whatever time the client sent (AsUtcDate keeps it): due tomorrow at 10:00.
        var r = await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(p, null, "with time", null, "todo", null, null,
            new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc), null, null, null), default);
        Assert.True(r.Success, r.Error);
        await svc.AssignUserAsync(owner.Id, r.Task!.Id, member.Id, null, default);
        gw.Sent.Clear();

        await ProjectAutomationWorker.RunOnceAsync(t.Verify(), gw, new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local), DateTime.UtcNow, NullLogger.Instance, default);
        Assert.Contains(gw.Sent, s => s.Event == "project.task_due_tomorrow" && s.User == member.Id);
    }

    [Fact]
    public async Task Default_statuses_are_created_once()
    {
        using var t = new TestDb();
        var owner = t.AddUser("AF_S");
        var p = (await new ProjectService(t.Verify()).CreateProjectAsync(owner.Id,
            new CreateProjectRequest("S", null, 2026, owner.Id, null, null, null, null), default)).Project!.Id;
        await new ProjectStatusService(t.Verify()).EnsureStatusesAsync(p, default);
        await new ProjectStatusService(t.Verify()).EnsureStatusesAsync(p, default);
        Assert.Equal(ProjectStatusDefaults.Options.Count, t.Verify().ProjectStatusOptions.Count(o => o.ProjectId == p));
    }

    private sealed class RecordingGateway : IPop.Application.Notifications.INotificationGatewayService
    {
        public List<(string Event, Guid? User)> Sent { get; } = [];
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            IPop.Application.Notifications.NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Sent.Add((eventType, target.UserId));
            return Task.FromResult(new IPop.Application.Notifications.NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<IPop.Application.Notifications.NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> EnqueueAsync(Guid clientId, IPop.Application.Notifications.NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }
}
