using IPop.Api.Endpoints;
using IPop.Application.Calls;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using IPop.Infrastructure.Notifications.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// /notifications redesign (2026-11-22): chat history one row per conversation, missed-call rows, the category
/// every row is filed under (Mentions / Meetings / iDesk were always empty before), and muting by category.
public class NotificationHistoryTests
{
    private sealed class NoTransport : IWebPushTransport
    {
        public bool IsConfigured => false;
        public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
            => Task.FromResult(PushSendOutcome.Accepted);
    }

    private static async Task ChatJob(TestDb t, Guid messageId)
    {
        await using (var db = t.Verify()) await new PushQueue().EnqueueAsync(db, "chat", messageId, default);
        await using var db2 = t.Verify();
        await new PushDispatcher(db2, new NoTransport(), new EphemeralDataProtectionProvider(), NullLogger<PushDispatcher>.Instance)
            .ProcessAsync(DateTime.UtcNow, default);
    }

    private static List<(string Title, string Status, string Body)> Rows(TestDb t, Guid userId)
    {
        using var db = t.Verify();
        return db.NotificationDeliveries.AsNoTracking().Where(d => d.UserId == userId && d.Request.EventType == "chat.message")
            .Select(d => new { d.Request.Title, d.Status, d.Request.Body }).AsEnumerable().Select(x => (x.Title, x.Status, x.Body)).ToList();
    }

    [Fact]
    public async Task ChatMessages_FoldIntoOneRowPerConversation_WithCount()
    {
        using var t = new TestDb();
        var ursula = t.AddUser("1"); var me = t.AddUser("2");
        var room = t.AddRoom("dm");
        t.AddMember(room.Id, ursula.Id); t.AddMember(room.Id, me.Id);

        await ChatJob(t, t.AddMessage(room.Id, ursula.Id, "halo").Id);
        Assert.Equal([("User 1", "delivered", "halo")], Rows(t, me.Id));

        await ChatJob(t, t.AddMessage(room.Id, ursula.Id, "bisa remote?").Id);
        await ChatJob(t, t.AddMessage(room.Id, ursula.Id, "sekarang ya").Id);
        var rows = Rows(t, me.Id);
        Assert.Single(rows);
        Assert.Equal("User 1 · 3 new messages", rows[0].Title);
        Assert.Equal("sekarang ya", rows[0].Body);
        Assert.Empty(Rows(t, ursula.Id));   // never for the sender
    }

    [Fact]
    public async Task GroupRow_IsNamedAfterTheGroup_AndSkipsMutedRoomsAndMentions()
    {
        using var t = new TestDb();
        var a = t.AddUser("1"); var b = t.AddUser("2"); var c = t.AddUser("3"); var d = t.AddUser("4");
        var room = t.AddRoom("group", "ICT Team");
        foreach (var u in new[] { a, b, c, d }) t.AddMember(room.Id, u.Id);
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = c.Id, RoomId = room.Id, IsMuted = true });
        var msg = t.AddMessage(room.Id, a.Id, "VPN down");
        t.Db.ChatMentions.Add(new ChatMention { Id = Guid.NewGuid(), MessageId = msg.Id, UserId = d.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        await ChatJob(t, msg.Id);
        Assert.Equal([("ICT Team", "delivered", "User 1: VPN down")], Rows(t, b.Id));
        Assert.Empty(Rows(t, c.Id));  // muted room
        Assert.Empty(Rows(t, d.Id));  // the mention row covers it
    }

    [Fact]
    public async Task ReadingTheChat_MarksTheRowRead_AndANewMessageBringsItBack()
    {
        using var t = new TestDb();
        var ursula = t.AddUser("1"); var me = t.AddUser("2");
        var room = t.AddRoom("dm");
        t.AddMember(room.Id, ursula.Id); t.AddMember(room.Id, me.Id);
        var m1 = t.AddMessage(room.Id, ursula.Id, "halo", at: DateTime.UtcNow.AddMinutes(-2));
        await ChatJob(t, m1.Id);

        // Not everything read yet → stays unread.
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = me.Id, RoomId = room.Id, LastReadMessageAt = m1.CreatedAt.AddSeconds(-1) });
        t.Db.SaveChanges();
        Assert.False(await ChatHistoryNotifications.SyncAfterReadAsync(t.Verify(), me.Id, room.Id, default));

        await using (var db = t.Verify())
        {
            var s = await db.ChatRoomUserSettings.FirstAsync(x => x.UserId == me.Id);
            s.LastReadMessageAt = m1.CreatedAt;
            await db.SaveChangesAsync();
        }
        Assert.True(await ChatHistoryNotifications.SyncAfterReadAsync(t.Verify(), me.Id, room.Id, default));
        Assert.Equal("read", Rows(t, me.Id).Single().Status);

        await ChatJob(t, t.AddMessage(room.Id, ursula.Id, "lagi").Id);
        var row = Rows(t, me.Id).Single();
        Assert.Equal(("User 1", "delivered"), (row.Title, row.Status));   // only the one unread counts
    }

    [Fact]
    public async Task AlreadyRead_WhenTheJobRuns_WritesNothing()
    {
        using var t = new TestDb();
        var ursula = t.AddUser("1"); var me = t.AddUser("2");
        var room = t.AddRoom("dm");
        t.AddMember(room.Id, ursula.Id); t.AddMember(room.Id, me.Id);
        var m = t.AddMessage(room.Id, ursula.Id, "halo");
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = me.Id, RoomId = room.Id, LastReadMessageAt = m.CreatedAt });
        t.Db.SaveChanges();
        await ChatJob(t, m.Id);
        Assert.Empty(Rows(t, me.Id));
    }

    [Fact]
    public async Task MissedCall_GetsOneRow_AndRecentsSeenReadsIt()
    {
        using var t = new TestDb();
        var dia = t.AddUser("1"); var me = t.AddUser("2");
        var callId = Guid.NewGuid();
        t.Db.CallRooms.Add(new CallRoom { Id = callId, Kind = "user", Status = CallStatus.Missed, CreatorId = dia.Id, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow, Version = 1 });
        t.Db.CallHistories.Add(new CallHistory { Id = Guid.NewGuid(), CallId = callId, PerformerId = me.Id, Direction = "incoming", Result = "missed", StartedAt = DateTime.UtcNow });
        t.Db.CallHistories.Add(new CallHistory { Id = Guid.NewGuid(), CallId = callId, PerformerId = dia.Id, Direction = "outgoing", Result = "no-answer", StartedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var room = new CallRoomDto(callId, "user", CallStatus.Missed, null, dia.Id, DateTime.UtcNow, null, DateTime.UtcNow, null,
            [new CallParticipantDto(dia.Id, "1", "Dia Kartika", "left", false, false, null), new CallParticipantDto(me.Id, "2", "Me", "invited", false, false, null)]);

        Assert.Equal([me.Id], await MissedCallNotifications.SyncAsync(t.Verify(), room, default));
        Assert.Empty(await MissedCallNotifications.SyncAsync(t.Verify(), room, default));   // repeat = no-op

        await using (var db = t.Verify())
        {
            var r = await db.NotificationDeliveries.Include(d => d.Request).SingleAsync(d => d.UserId == me.Id);
            Assert.Equal("Missed call from Dia Kartika", r.Request.Title);
            Assert.Equal("meeting", NotificationCategory.Of(r.Request.EventType, r.Request.TargetUrl));
        }
        Assert.Equal(1, await MissedCallNotifications.MarkSeenAsync(t.Verify(), me.Id, null, default));
    }

    [Theory]
    [InlineData("chat.mention", "/chat/x?jump=y", "mention")]
    [InlineData("note.mention", "/notes/x", "mention")]
    [InlineData("chat.message", "/chat/x", "chat")]
    [InlineData("chat.emergency", "/chat/x", "chat")]
    [InlineData("ticket.message_created", "/tickets/x#messages", "tickets")]
    [InlineData("system", "/idesk/tickets/x#update", "tickets")]
    [InlineData("meeting.reminder", "/calls?tab=scheduled", "meeting")]
    [InlineData("calendar.share.ask", "/calendar", "meeting")]
    [InlineData("call.missed", "/calls", "meeting")]
    [InlineData("mail.new", "/mail?mailId=x", "mail")]
    [InlineData("project.task_assigned", "/projects/x?task=y", "task")]
    [InlineData("memo.mandatory", "/news/x", "news")]
    [InlineData("system.epop_import", "/mail", "system")]
    [InlineData("note.comment", "/notes/x", "system")]
    [InlineData("hrsync", null, "system")]
    public void Category_FollowsEventTypeThenLink(string eventType, string? url, string expected)
        => Assert.Equal(expected, NotificationCategory.Of(eventType, url));

    [Fact]
    public void Muting_IsPerCategory_NotTheOldCoarseBucket()
    {
        // The page saves categories; "system" used to silence iDesk, meetings and notes together.
        Assert.False(NotificationCategory.IsMuted(["system"], "ticket.assigned", "/tickets/x", "system"));
        Assert.True(NotificationCategory.IsMuted(["tickets"], "ticket.assigned", "/tickets/x", "system"));
        Assert.True(NotificationCategory.IsMuted(["meeting"], "meeting.reminder", "/calls", "system"));
        Assert.True(NotificationCategory.IsMuted(["task"], "project.task_assigned", null, "project"));
        // Old saved values keep working for their own kind.
        Assert.True(NotificationCategory.IsMuted(["project"], "project.task_assigned", null, "project"));
        Assert.True(NotificationCategory.IsMuted(["call"], "meeting.invited", null, "system"));
        // Muting Chat does not silence mentions.
        Assert.False(NotificationCategory.IsMuted(["chat"], "chat.mention", "/chat/x", "chat"));
        Assert.True(NotificationCategory.IsMuted(["chat"], "chat.message", "/chat/x", "chat"));
        Assert.True(NotificationCategory.IsMuted(["ticket.assigned"], "ticket.assigned", null, "system"));
    }

    [Fact]
    public async Task Inbox_FiltersByCategory_AndCountsUnreadPerCategory()
    {
        using var t = new TestDb();
        var me = t.AddUser("1");
        var c = t.AddNotificationClient("x", "h", null, null, 0, 1000, true);
        void Add(string ev, string? url, string status)
        {
            var r = new NotificationRequest { Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = Guid.NewGuid().ToString(), Priority = "normal",
                EventType = ev, Title = ev, Body = "b", TargetUrl = url, Status = "delivered", AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            t.Db.NotificationRequests.Add(r);
            t.Db.NotificationDeliveries.Add(new NotificationDelivery { Id = Guid.NewGuid(), RequestId = r.Id, UserId = me.Id, Status = status,
                DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            t.Db.SaveChanges();
        }
        Add("chat.mention", "/chat/a", "delivered");
        Add("ticket.sent", "/tickets/a", "delivered");
        Add("ticket.sent", "/tickets/b", "read");
        Add("meeting.reminder", "/calls", "delivered");

        var all = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 50, default);
        Assert.Equal(1, all.UnreadByCategory!["tickets"]);
        Assert.Equal(1, all.UnreadByCategory["mention"]);
        Assert.Equal(1, all.UnreadByCategory["meeting"]);
        Assert.Contains(all.Items, i => i.Category == "tickets");

        var tickets = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 50, default, "tickets");
        Assert.Equal(2, tickets.TotalCount);
        var search = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 50, default, null, "MEETING");
        Assert.Single(search.Items);
    }
}
