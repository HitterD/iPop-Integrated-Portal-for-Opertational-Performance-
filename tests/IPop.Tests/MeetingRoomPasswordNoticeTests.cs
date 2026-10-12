using IPop.Api.Endpoints;
using IPop.Application.Hall;
using IPop.Application.Notifications;
using IPop.Infrastructure.Meetings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Meeting rooms v2 §1.4 (Task 8): the booking's password reaches the organizer and invitees in the invite and the
/// reminder notification text ("Room password: 123 456"), only for SJA Hall room meetings, never in links.
public class MeetingRoomPasswordNoticeTests
{
    private sealed class CapturingGateway : INotificationGatewayService
    {
        public List<(string EventType, string Title, string Message, NotificationTarget Target, string? Url)> Calls { get; } = [];

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Calls.Add((eventType, title, message, target, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static MeetingEndpoints.CreateMeetingRequest Req(DateTime start, string location, int? reminder, IReadOnlyList<string>? niks = null) =>
        new("Weekly ICT sync", start, start.AddMinutes(60), null, location, null, null, null, reminder, null, niks, null, null);

    [Theory]
    [InlineData("482917", "482 917")]
    [InlineData("000123", "000 123")]
    public void Password_is_shown_as_three_plus_three_digits(string stored, string shown) =>
        Assert.Equal(shown, RoomPasswords.Format(stored));

    [Fact]
    public async Task Invite_for_a_hall_room_meeting_carries_the_formatted_password_but_not_in_the_link()
    {
        using var t = new TestDb();
        var org = t.AddUser("PWN_ORG");
        var a = t.AddUser("PWN_A");
        var (_, id, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id,
            Req(DateTime.UtcNow.AddDays(1), "SJA Hall · Toraja", 15, ["PWN_A"]), default);
        await using var v = t.Verify();
        var password = (await v.Meetings.SingleAsync(m => m.Id == id)).RoomPassword!;
        var dto = (await MeetingEndpoints.LoadUpcomingAsync(v, org.Id, DateTime.UtcNow, default)).Single();

        var gateway = new CapturingGateway();
        await MeetingEndpoints.PublishInvitedAsync(gateway, dto, invited, default);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal([a.Id], call.Target.UserIds);
        Assert.Contains($"Room password: {password[..3]} {password[3..]}", call.Message);
        Assert.DoesNotContain(password, call.Url ?? "");
        Assert.DoesNotContain(password[..3], call.Title);
    }

    [Fact]
    public async Task Invite_for_another_place_has_no_password_line()
    {
        using var t = new TestDb();
        var org = t.AddUser("PWN2_ORG");
        t.AddUser("PWN2_A");
        var (_, _, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id,
            Req(DateTime.UtcNow.AddDays(1), "Online · iPop Meeting", 15, ["PWN2_A"]), default);
        await using var v = t.Verify();
        var dto = (await MeetingEndpoints.LoadUpcomingAsync(v, org.Id, DateTime.UtcNow, default)).Single();

        var gateway = new CapturingGateway();
        await MeetingEndpoints.PublishInvitedAsync(gateway, dto, invited, default);

        Assert.DoesNotContain("password", Assert.Single(gateway.Calls).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invite_text_helper_only_adds_the_password_for_hall_rooms()
    {
        var start = new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);
        var hall = MeetingEndpoints.InviteMessage("Sari", "Sync", start, "SJA Hall · Kalosi", "123456");
        Assert.EndsWith(" Room password: 123 456", hall);
        Assert.StartsWith("Sari invited you to \"Sync\" on ", hall);
        // A stale password on a meeting that moved out of the hall, or no password at all: no line.
        Assert.DoesNotContain("password", MeetingEndpoints.InviteMessage("Sari", "Sync", start, "Lantai 3", "123456"));
        Assert.DoesNotContain("password", MeetingEndpoints.InviteMessage("Sari", "Sync", start, "SJA Hall · Kalosi", null));
    }

    [Theory]
    [InlineData("SJA Hall · Kalosi", true)]
    [InlineData("Online · iPop Meeting", false)]
    public async Task Reminder_for_a_hall_room_meeting_carries_the_formatted_password(string location, bool hallRoom)
    {
        using var t = new TestDb();
        var org = t.AddUser("PWR_ORG");
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(DateTime.UtcNow.AddMinutes(5), location, 1), default);
        var reminder = await t.Db.MeetingReminders.SingleAsync(r => r.MeetingId == id);
        reminder.SendAt = DateTime.UtcNow.AddMinutes(-1);
        await t.Db.SaveChangesAsync();
        var password = (await t.Db.Meetings.SingleAsync(m => m.Id == id)).RoomPassword;

        var gateway = new CapturingGateway();
        Assert.Equal(1, await MeetingReminderWorker.ProcessDueRemindersAsync(t.Db, gateway, default));

        var call = Assert.Single(gateway.Calls);
        if (hallRoom)
        {
            Assert.NotNull(password);
            Assert.Contains($"Room password: {RoomPasswords.Format(password!)}", call.Message);
            Assert.DoesNotContain(password!, call.Url ?? "");
        }
        else
        {
            Assert.Null(password);
            Assert.DoesNotContain("password", call.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Calendar_invite_for_a_hall_room_meeting_carries_the_password()
    {
        using var t = new TestDb();
        var me = t.AddUser("PWC_ORG"); var guest = t.AddUser("PWC_A");
        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Utc);
        var (r, ev, invited) = await CalendarEndpoints.CreateEventAsync(t.Db, me.Id,
            new CalendarEndpoints.SaveEventRequest("Sync", day.AddHours(3), day.AddHours(4), Location: "SJA Hall · Robusta", AttendeeIds: [guest.Id]), default);
        Assert.Equal(CalendarEndpoints.SaveResult.Ok, r);
        var password = (await t.Db.Meetings.SingleAsync(m => m.Id == ev!.MeetingId)).RoomPassword!;

        var gateway = new CapturingGateway();
        await CalendarEndpoints.NotifyInvitedAsync(t.Db, gateway, ev!, me.Id, invited, null, default);

        var invite = gateway.Calls.Single(c => c.Target.UserIds?.Contains(guest.Id) == true);
        Assert.Contains($"Room password: {RoomPasswords.Format(password)}", invite.Message);
        Assert.DoesNotContain(password, invite.Url ?? "");
    }
}
