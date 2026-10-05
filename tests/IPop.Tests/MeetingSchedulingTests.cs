using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Meetings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Calls › Scheduled: create honours the reminder choice and notifies invitees, cancel uses one
/// status and cleans up, the reminder worker skips cancelled meetings, /upcoming is one query.
public class MeetingSchedulingTests
{
    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string EventType, NotificationTarget Target, string? Url)> Calls { get; } = [];

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Calls.Add((eventType, target, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static MeetingEndpoints.CreateMeetingRequest Req(DateTime start, int? reminder, IReadOnlyList<string>? niks = null, Guid? roomId = null) =>
        new("Sprint review", start, start.AddMinutes(30), null, "Online · iPop Meeting", null, null, null, reminder, roomId, niks, null, "Demo");

    [Fact]
    public async Task Create_HonoursChosenReminder_OnePerAttendee()
    {
        using var t = new TestDb();
        var org = t.AddUser("SCH_ORG");
        var a = t.AddUser("SCH_A");
        var start = DateTime.UtcNow.AddDays(2);

        var (result, id, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(start, 15, ["SCH_A"]), default);

        Assert.Equal(MeetingEndpoints.CreateResult.Created, result);
        Assert.Equal([a.Id], invited);
        await using var v = t.Verify();
        var m = await v.Meetings.SingleAsync(x => x.Id == id);
        Assert.Equal(15, m.ReminderMinutes);
        var reminders = await v.MeetingReminders.Where(r => r.MeetingId == id).ToListAsync();
        Assert.Equal(2, reminders.Count);
        Assert.All(reminders, r => Assert.Equal(start.AddMinutes(-15), r.SendAt, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await v.CalendarEvents.CountAsync(e => e.MeetingId == id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Create_NoReminder_CreatesNoReminderRows(int? reminder)
    {
        using var t = new TestDb();
        var org = t.AddUser("SCH2_ORG");
        t.AddUser("SCH2_A");

        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(DateTime.UtcNow.AddDays(1), reminder, ["SCH2_A"]), default);

        await using var v = t.Verify();
        Assert.Null((await v.Meetings.SingleAsync(x => x.Id == id)).ReminderMinutes);
        Assert.Equal(0, await v.MeetingReminders.CountAsync(r => r.MeetingId == id));
    }

    [Fact]
    public async Task Create_RejectsEndBeforeStart_AndForeignGroup()
    {
        using var t = new TestDb();
        var org = t.AddUser("SCH3_ORG");
        var start = DateTime.UtcNow.AddDays(1);
        var bad = Req(start, 30) with { EndAt = start.AddMinutes(-5) };
        Assert.Equal(MeetingEndpoints.CreateResult.InvalidRange, (await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, bad, default)).Result);

        var room = t.AddRoom("group", "Ops");
        Assert.Equal(MeetingEndpoints.CreateResult.NotRoomMember,
            (await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(start, 30, roomId: room.Id), default)).Result);
    }

    [Fact]
    public async Task Create_WithGroup_LinksRoomId_AndNotifiesInviteesButNotOrganizer()
    {
        using var t = new TestDb();
        var org = t.AddUser("SCH4_ORG");
        var a = t.AddUser("SCH4_A");
        var b = t.AddUser("SCH4_B");
        var room = t.AddRoom("group", "Ops");
        t.AddMember(room.Id, org.Id);
        t.AddMember(room.Id, a.Id);
        t.AddMember(room.Id, b.Id);

        // The client expands the group to its members' NIKs and passes RoomId.
        var (_, id, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id,
            Req(DateTime.UtcNow.AddDays(1), 30, ["SCH4_A", "SCH4_B", "SCH4_ORG"], room.Id), default);
        await using var v = t.Verify();
        Assert.Equal(room.Id, (await v.Meetings.SingleAsync(x => x.Id == id)).RoomId);

        var dto = (await MeetingEndpoints.LoadUpcomingAsync(v, org.Id, DateTime.UtcNow, default)).Single();
        var gateway = new RecordingGateway();
        await MeetingEndpoints.PublishInvitedAsync(gateway, dto, invited, default);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal("meeting.invited", call.EventType);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), call.Target.UserIds!.OrderBy(x => x));
        Assert.DoesNotContain(org.Id, call.Target.UserIds!);
        Assert.Equal($"/calls?tab=scheduled&meeting={id}", call.Url);
    }

    [Fact]
    public async Task Create_RejectsOversizedTitleAndDoesNotQueuePastReminder()
    {
        using var t = new TestDb(); var org = t.AddUser("MAIL_RANGE");
        var start = DateTime.UtcNow.AddMinutes(5);
        var bad = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(start, 30) with { Title = new string('x', 201) }, default);
        Assert.Equal(MeetingEndpoints.CreateResult.InvalidTitle, bad.Result);
        Assert.Empty(t.Db.Meetings.Local);
        var (result, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(start, 30), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, result);
        await using var v = t.Verify();
        Assert.Equal(30, (await v.Meetings.SingleAsync(x => x.Id == id)).ReminderMinutes);
        Assert.Empty(await v.MeetingReminders.Where(x => x.MeetingId == id).ToListAsync());
    }

    [Fact]
    public async Task Create_UsesCurrentGroupMembersAndCalendarDetails()
    {
        using var t = new TestDb();
        var org = t.AddUser("MAIL_ORG"); var current = t.AddUser("MAIL_CURRENT");
        var departed = t.AddUser("MAIL_LEFT"); var explicitGuest = t.AddUser("MAIL_EXTRA");
        var room = t.AddRoom("group", "Mail team");
        t.AddMember(room.Id, org.Id); t.AddMember(room.Id, current.Id); t.AddMember(room.Id, departed.Id);
        (await t.Db.ChatRoomMembers.SingleAsync(x => x.RoomId == room.Id && x.UserId == departed.Id)).LeftAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();
        var start = DateTime.UtcNow.AddDays(2);
        var (_, id, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id,
            Req(start, 15, ["MAIL_EXTRA", "MAIL_CURRENT"], room.Id) with { Location = "  Office  ", Link = " https://example.test " }, default);
        Assert.Equal(new[] { current.Id, explicitGuest.Id }.OrderBy(x => x), invited.OrderBy(x => x));
        await using var v = t.Verify();
        var ev = await v.CalendarEvents.SingleAsync(x => x.MeetingId == id);
        Assert.Equal("Office", ev.Location);
        Assert.Equal("https://example.test", ev.Link);
        Assert.Equal("Demo", ev.Notes);
        Assert.Equal(15, ev.ReminderMinutes);
        Assert.Equal((await v.Meetings.SingleAsync(x => x.Id == id)).Visibility, ev.Visibility);
        Assert.Equal(3, await v.MeetingReminders.CountAsync(x => x.MeetingId == id));
    }

    [Fact]
    public async Task Cancel_SetsOneStatus_DeletesPendingRemindersAndCalendarRows()
    {
        using var t = new TestDb();
        var org = t.AddUser("CAN_ORG");
        var a = t.AddUser("CAN_A");
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(DateTime.UtcNow.AddDays(1), 60, ["CAN_A"]), default);
        // One reminder already went out — it stays as history.
        var sent = await t.Db.MeetingReminders.FirstAsync(r => r.MeetingId == id && r.UserId == a.Id);
        sent.SentAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();

        var (result, _, notify) = await MeetingEndpoints.ApplyCancelAsync(t.Db, id!.Value, org.Id, default);

        Assert.Equal(MeetingEndpoints.CancelResult.Applied, result);
        Assert.Equal([a.Id], notify);
        await using var v = t.Verify();
        Assert.Equal(MeetingStatus.Cancelled, (await v.Meetings.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal([sent.Id], await v.MeetingReminders.Where(r => r.MeetingId == id).Select(r => r.Id).ToListAsync());
        Assert.Equal(0, await v.CalendarEvents.CountAsync(e => e.MeetingId == id));
        Assert.Empty(await MeetingEndpoints.LoadUpcomingAsync(v, a.Id, DateTime.UtcNow, default));

        // Second cancel is a no-op (no second notification); non-organizers are refused.
        Assert.Equal(MeetingEndpoints.CancelResult.AlreadyCancelled, (await MeetingEndpoints.ApplyCancelAsync(t.Db, id.Value, org.Id, default)).Result);
        Assert.Equal(MeetingEndpoints.CancelResult.Forbidden, (await MeetingEndpoints.ApplyCancelAsync(t.Db, id.Value, a.Id, default)).Result);
    }

    [Fact]
    public async Task ReminderWorker_SkipsCancelledMeetings()
    {
        using var t = new TestDb();
        var org = t.AddUser("RW_ORG");
        var live = new Meeting { Id = Guid.NewGuid(), Title = "Live", StartAt = DateTime.UtcNow.AddMinutes(5), EndAt = DateTime.UtcNow.AddMinutes(35), OrganizerId = org.Id, CreatedAt = DateTime.UtcNow };
        var dead = new Meeting { Id = Guid.NewGuid(), Title = "Dead", StartAt = DateTime.UtcNow.AddMinutes(5), EndAt = DateTime.UtcNow.AddMinutes(35), OrganizerId = org.Id, Status = MeetingStatus.Cancelled, CreatedAt = DateTime.UtcNow };
        t.Db.Meetings.AddRange(live, dead);
        foreach (var m in new[] { live, dead })
            t.Db.MeetingReminders.Add(new MeetingReminder { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = org.Id, SendAt = DateTime.UtcNow.AddMinutes(-1) });
        await t.Db.SaveChangesAsync();

        var gateway = new RecordingGateway();
        var sent = await MeetingReminderWorker.ProcessDueRemindersAsync(t.Db, gateway, default);

        Assert.Equal(1, sent);
        Assert.Single(gateway.Calls);
        await using var v = t.Verify();
        Assert.Null((await v.MeetingReminders.SingleAsync(r => r.MeetingId == dead.Id)).SentAt);
    }

    [Fact]
    public async Task Upcoming_ReturnsMineWithAttendees_SoonestFirst_ExcludingEndedAndOthers()
    {
        using var t = new TestDb();
        var me = t.AddUser("UP_ME");
        var other = t.AddUser("UP_OTHER");
        var now = DateTime.UtcNow;
        await MeetingEndpoints.ApplyCreateAsync(t.Db, other.Id, Req(now.AddDays(2), 30, ["UP_ME"]) with { Title = "Later" }, default);
        await MeetingEndpoints.ApplyCreateAsync(t.Db, me.Id, Req(now.AddHours(1), 30) with { Title = "Sooner" }, default);
        await MeetingEndpoints.ApplyCreateAsync(t.Db, other.Id, Req(now.AddHours(3), 30) with { Title = "Not mine" }, default);
        await MeetingEndpoints.ApplyCreateAsync(t.Db, me.Id, Req(now.AddHours(-3), 30) with { Title = "Over" }, default);

        await using var v = t.Verify();
        var list = await MeetingEndpoints.LoadUpcomingAsync(v, me.Id, now, default);

        Assert.Equal(["Sooner", "Later"], list.Select(m => m.Title));
        var later = list[1];
        Assert.Equal(2, later.Attendees.Count);
        Assert.Equal("pending", later.MyResponse);
        Assert.Equal(30, later.ReminderMinutes);
        Assert.Equal(MeetingEndpoints.RoomNameFor(later.Id), later.RoomName);
    }
}
