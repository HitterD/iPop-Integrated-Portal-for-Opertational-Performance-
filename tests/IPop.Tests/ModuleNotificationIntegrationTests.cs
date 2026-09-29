using IPop.Application.Hris;
using IPop.Application.Mail;
using IPop.Application.News;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Hris;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

public class ModuleNotificationIntegrationTests
{
    private sealed class MockNotificationGatewayService : INotificationGatewayService
    {
        public List<PublishedCall> Calls { get; } = [];

        public record PublishedCall(
            string ClientSlug,
            string EventType,
            string Title,
            string Message,
            NotificationTarget Target,
            string Priority,
            string? TargetUrl);

        public Task<NotificationEnqueueResult> PublishInternalAsync(
            string clientSlug,
            string eventType,
            string title,
            string message,
            NotificationTarget target,
            string priority = "normal",
            string? targetUrl = null,
            CancellationToken ct = default)
        {
            Calls.Add(new PublishedCall(clientSlug, eventType, title, message, target, priority, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task MailService_SendAsync_PublishesInternalNotification_ToRecipientsOnly()
    {
        using var t = new TestDb();
        var sender = t.AddUser("20080115");
        var recipient1 = t.AddUser("20140312");
        var recipient2 = t.AddUser("20200501");

        var mockGateway = new MockNotificationGatewayService();
        var mailSvc = new MailService(t.Verify(), mockGateway);

        var req = new ComposeMailRequest(
            Subject: "Rapat Sprint Planning",
            Body: "Halo tim, mari bergabung pada rapat sprint planning besok pagi.",
            To: [recipient1.Nik, recipient2.Nik],
            Cc: [sender.Nik],
            Bcc: []); // Sender in CC should be filtered out from notification recipients

        var result = await mailSvc.SendAsync(sender.Id, req, default);
        Assert.True(result.Success);

        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("mail", call.ClientSlug);
        Assert.Equal("mail.new", call.EventType);
        // Deep-links to the message itself, not just the mailbox.
        Assert.Equal($"/mail?mailId={result.MailId}", call.TargetUrl);
        Assert.Contains(sender.Name, call.Title);
        Assert.NotNull(call.Target.UserIds);
        Assert.Contains(recipient1.Id, call.Target.UserIds!);
        Assert.Contains(recipient2.Id, call.Target.UserIds!);
        Assert.DoesNotContain(sender.Id, call.Target.UserIds!);
    }

    [Fact]
    public async Task ProjectService_AssignUserAsync_PublishesInternalNotification_WithProjectTargetUrl()
    {
        using var t = new TestDb();
        var manager = t.AddUser("20080115");
        var assignee = t.AddUser("20140312");

        var mockGateway = new MockNotificationGatewayService();
        var projectSvc = new ProjectService(t.Verify(), mockGateway);

        var projectRes = await projectSvc.CreateProjectAsync(manager.Id, new CreateProjectRequest(
            Name: "iPop Redesign",
            Description: "Deskripsi",
            Year: 2026,
            OwnerId: manager.Id,
            StartDate: null,
            Deadline: null,
            Risk: null,
            Obstacle: null), default);
        Assert.True(projectRes.Success);
        var projectId = projectRes.Project!.Id;

        // Add assignee to project
        await projectSvc.AddMemberAsync(manager.Id, projectId, new AddProjectMemberRequest(assignee.Id, "member"), default);
        mockGateway.Calls.Clear(); // Clear project member add call to test AssignUserAsync specifically

        var taskRes = await projectSvc.CreateTaskAsync(manager.Id, new CreateTaskRequest(
            ProjectId: projectId,
            ParentTaskId: null,
            Title: "Migrasi Komponen SignalR",
            Description: "Kerjakan migrasi",
            Status: "todo",
            Priority: "high",
            StartDate: null,
            DueDate: null,
            EstimatedHours: 4,
            Dependencies: null,
            Tags: null), default);
        Assert.True(taskRes.Success);
        var taskId = taskRes.Task!.Id;

        var assignRes = await projectSvc.AssignUserAsync(manager.Id, taskId, assignee.Id, "developer", default);
        Assert.True(assignRes.Success);

        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("projects", call.ClientSlug);
        Assert.Equal("project.task_assigned", call.EventType);
        Assert.Equal($"/projects/{projectId}?task={taskId}", call.TargetUrl);
        Assert.Equal(assignee.Id, call.Target.UserId);
        Assert.Contains("Migrasi Komponen SignalR", call.Title);
    }

    [Fact]
    public async Task ProjectService_AddMemberAsync_PublishesInternalNotification_WithProjectTargetUrl()
    {
        using var t = new TestDb();
        var manager = t.AddUser("20080115");
        var newMember = t.AddUser("20140312");

        var mockGateway = new MockNotificationGatewayService();
        var projectSvc = new ProjectService(t.Verify(), mockGateway);

        var projectRes = await projectSvc.CreateProjectAsync(manager.Id, new CreateProjectRequest(
            Name: "Intranet Refresh",
            Description: "Deskripsi",
            Year: 2026,
            OwnerId: manager.Id,
            StartDate: null,
            Deadline: null,
            Risk: null,
            Obstacle: null), default);
        Assert.True(projectRes.Success);
        var projectId = projectRes.Project!.Id;

        mockGateway.Calls.Clear();

        var addRes = await projectSvc.AddMemberAsync(manager.Id, projectId, new AddProjectMemberRequest(newMember.Id, "member"), default);
        Assert.True(addRes.Success);

        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("projects", call.ClientSlug);
        Assert.Equal("project.member_added", call.EventType);
        Assert.Equal($"/projects/{projectId}", call.TargetUrl);
        Assert.Equal(newMember.Id, call.Target.UserId);
        Assert.Contains("Intranet Refresh", call.Title);
    }

    [Fact]
    public async Task NewsService_PublishAsync_PublishesInternalNotification_WithNewsTargetUrl()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("20080115");
        var author = t.AddUser("20140312");

        var mockGateway = new MockNotificationGatewayService();
        var newsSvc = new NewsService(t.Verify(), mockGateway);

        var createRes = await newsSvc.CreateAsync(author.Id, new CreateNewsRequest(
            Type: "news",
            Title: "Hari Libur Nasional Tambahan",
            Body: "Diumumkan bahwa besok adalah hari libur bersama."), default);
        Assert.True(createRes.Success);
        var newsId = createRes.News!.Id;

        await newsSvc.SubmitAsync(author.Id, newsId, default);
        mockGateway.Calls.Clear();

        var pubRes = await newsSvc.PublishAsync(admin.Id, newsId, default);
        Assert.True(pubRes.Success);

        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("news", call.ClientSlug);
        Assert.Equal("news.published", call.EventType);
        Assert.Equal($"/news/{newsId}", call.TargetUrl);
        Assert.Equal("normal", call.Priority);
        Assert.True(call.Target.All);
        Assert.Contains("Hari Libur Nasional Tambahan", call.Title);
    }

    [Fact]
    public async Task NewsService_PublishAsync_MandatoryMemo_PublishesHighPriorityMemoNotification()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("20080115");

        var mockGateway = new MockNotificationGatewayService();
        var newsSvc = new NewsService(t.Verify(), mockGateway);

        var createRes = await newsSvc.CreateAsync(admin.Id, new CreateNewsRequest(
            Type: "memo",
            Title: "Kepatuhan Keamanan Informasi",
            Body: "Seluruh pegawai wajib membaca dan memahami panduan keamanan.",
            RequiresAcknowledgement: true), default);
        Assert.True(createRes.Success);
        var newsId = createRes.News!.Id;

        await newsSvc.SubmitAsync(admin.Id, newsId, default);
        mockGateway.Calls.Clear();

        var pubRes = await newsSvc.PublishAsync(admin.Id, newsId, default);
        Assert.True(pubRes.Success);

        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("news", call.ClientSlug);
        Assert.Equal("memo.mandatory", call.EventType);
        Assert.Equal($"/news/{newsId}", call.TargetUrl);
        Assert.Equal("high", call.Priority);
        Assert.True(call.Target.All);
        Assert.Contains("[Wajib Baca]", call.Title);
    }

    private sealed class StubHrisGateway(IReadOnlyList<HrisEmployee> employees) : IHrisGateway
    {
        public Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? since, CancellationToken ct)
            => Task.FromResult(employees);
    }

    [Fact]
    public async Task HrisSyncService_RunAsync_WhenChangesOccur_PublishesSystemNotification()
    {
        using var t = new TestDb();
        var mockGateway = new MockNotificationGatewayService();
        var feed = new List<HrisEmployee>
        {
            new("99990001", "Bagas", "bagas@example.com", "Dev", "IT", "IT Dept", null, null, "123", "Jakarta", true, DateTime.UtcNow)
        };

        var syncSvc = new HrisSyncService(t.Verify(), new StubHrisGateway(feed), mockGateway);
        var result = await syncSvc.RunAsync(default);

        Assert.Equal(1, result.Inserted);
        Assert.Single(mockGateway.Calls);
        var call = mockGateway.Calls[0];
        Assert.Equal("system", call.ClientSlug);
        Assert.Equal("system.hris_sync", call.EventType);
        Assert.Equal("/notifications/inbox", call.TargetUrl);
        Assert.True(call.Target.All);
        Assert.Contains("Sinkronisasi HRIS Selesai", call.Title);
    }
}
