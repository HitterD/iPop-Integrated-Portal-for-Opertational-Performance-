using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// The reminder pass looks at the earliest due tasks first, so hundreds of far-future reminders can never crowd
/// out one that is due now (the old unordered Take(500) could).
public class TaskReminderWorkerTests
{
    private sealed class Gateway : INotificationGatewayService
    {
        public List<string> Titles { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Titles.Add(title);
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static ProjectTask Task(TestDb t, Project p, Guid assignee, string title, DateTime due)
    {
        var task = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = p.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == p.Id).Id,
            Title = title, Status = "todo", DueDate = due, CreatedById = p.OwnerId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.ProjectTasks.Add(task);
        t.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = assignee });
        return task;
    }

    [Fact]
    public async Task A_due_reminder_is_sent_even_with_600_future_ones_queued()
    {
        using var t = new TestDb();
        var u = t.AddUser("TR1");
        var p = t.AddProject("Plant upgrade", u.Id);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 600; i++)
        {
            var far = Task(t, p, u.Id, $"Later {i}", now.AddDays(30 + i % 50));
            t.Db.ProjectTaskReminders.Add(new ProjectTaskReminder { Id = Guid.NewGuid(), TaskId = far.Id, OffsetHours = 24, CreatedAt = now });
        }
        var soon = Task(t, p, u.Id, "Submit report", now.AddHours(10));
        var reminder = new ProjectTaskReminder { Id = Guid.NewGuid(), TaskId = soon.Id, OffsetHours = 24, CreatedAt = now };
        t.Db.ProjectTaskReminders.Add(reminder);
        t.Db.SaveChanges();

        var gw = new Gateway();
        using IPopDbContext db = t.Verify();
        await TaskReminderWorker.RunOnceAsync(db, gw, now, NullLogger.Instance, default);

        Assert.Equal(["Due Date Reminder: Submit report"], gw.Titles);
        Assert.NotNull(t.Verify().ProjectTaskReminders.Single(r => r.Id == reminder.Id).SentAt);
    }
}
