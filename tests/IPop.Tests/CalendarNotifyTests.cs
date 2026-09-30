using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;

namespace IPop.Tests;

/// Calendar save returned 500 although the event was saved: the invite publish was
/// fire-and-forget (`_ = notifs.PublishInternalAsync(...)`) on the request's DbContext and
/// raced LoadDetailAsync ("A second operation was started on this context"). There is no
/// WebApplicationFactory host in this repo, so these drive the endpoint helpers directly:
/// the publish must be finished before the helper returns, and must never throw.
public class CalendarNotifyTests
{
    /// Uses the same DbContext as the caller, like the real NotificationGatewayService.
    private sealed class DbBoundGateway(IPopDbContext db, bool fail = false) : INotificationGatewayService
    {
        public List<string> Completed { get; } = [];

        public async Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            await Task.Delay(30, ct);
            await db.Users.CountAsync(ct);
            if (fail) throw new InvalidOperationException("gateway down");
            Completed.Add(eventType);
            return new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow);
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);

    [Fact]
    public async Task Create_with_invitee_finishes_publish_before_detail_load()
    {
        using var t = new TestDb();
        var me = t.AddUser("CN1"); var guest = t.AddUser("CN2");
        var gateway = new DbBoundGateway(t.Db);

        var (r, ev, invited) = await CreateEventAsync(t.Db, me.Id,
            new SaveEventRequest("Sync", Day(2).AddHours(3), Day(2).AddHours(4), AttendeeIds: [guest.Id]), default);
        Assert.Equal(SaveResult.Ok, r);

        await NotifyInvitedAsync(t.Db, gateway, ev!, me.Id, invited, null, default);
        Assert.Equal(["meeting.invited"], gateway.Completed);   // awaited, not fire-and-forget

        // The endpoint's next step on the same context — used to throw concurrently.
        var detail = await LoadDetailAsync(t.Db, ev!.Id, me.Id, default);
        Assert.NotNull(detail);
        Assert.Contains(detail!.Attendees, p => p.UserId == guest.Id);
    }

    [Fact]
    public async Task Failing_publish_never_fails_the_save()
    {
        using var t = new TestDb();
        var me = t.AddUser("CN3"); var guest = t.AddUser("CN4");
        var gateway = new DbBoundGateway(t.Db, fail: true);

        var (_, ev, invited) = await CreateEventAsync(t.Db, me.Id,
            new SaveEventRequest("Sync", Day(2).AddHours(3), Day(2).AddHours(4), AttendeeIds: [guest.Id]), default);
        await NotifyInvitedAsync(t.Db, gateway, ev!, me.Id, invited, null, default);
        await MeetingEndpoints.PublishBestEffortAsync(gateway, null, "meeting.cancelled", "t", "m", [guest.Id], "high", "/calendar");

        Assert.NotNull(await LoadDetailAsync(t.Db, ev!.Id, me.Id, default));
    }

    [Fact]
    public async Task Best_effort_publish_skips_without_gateway_or_recipients_and_ignores_request_cancellation()
    {
        using var t = new TestDb();
        var gateway = new DbBoundGateway(t.Db);
        await MeetingEndpoints.PublishBestEffortAsync(null, null, "meeting.updated", "t", "m", [Guid.NewGuid()], "normal", null);
        await MeetingEndpoints.PublishBestEffortAsync(gateway, null, "meeting.updated", "t", "m", [], "normal", null);
        Assert.Empty(gateway.Completed);

        // The request token isn't passed through: a client disconnect can't abort a publish midway.
        await MeetingEndpoints.PublishBestEffortAsync(gateway, null, "meeting.updated", "t", "m", [Guid.NewGuid()], "normal", null);
        Assert.Equal(["meeting.updated"], gateway.Completed);
    }
}
