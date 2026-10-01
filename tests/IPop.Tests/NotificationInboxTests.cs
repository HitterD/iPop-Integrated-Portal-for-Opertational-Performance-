using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Per-user notification inbox (task: API list per user dengan filter + pagination):
/// ListInboxCore — the handler body of GET /api/notifications/v1/inbox. Seam mirrors
/// NotificationGatewayReadEndpointsTests (SQLite in-memory via TestDb).
public class NotificationInboxTests
{
    private static NotificationGatewayClient AddClient(TestDb t, string clientId = "hr")
        => t.AddNotificationClient(clientId, "h:secret123", null, null, 0, 1000, true);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string hash, string password) => hash == "h:" + password;
    }

    private sealed class NoopBroadcaster : INotificationRealtimeBroadcaster
    {
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static NotificationGatewayService Svc(TestDb t)
        => new(t.Verify(), new FakeHasher(), NullLogger<NotificationGatewayService>.Instance, new NoopBroadcaster());

    private static NotificationDelivery AddDelivery(
        TestDb t, NotificationGatewayClient c, Guid userId,
        string eventType, string status, string title, string idem, DateTime? deliveredAt = null)
    {
        var req = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = c.Id,
            IdempotencyKey = idem,
            Priority = "normal",
            EventType = eventType,
            Title = title,
            Body = $"body-{idem}",
            Status = NotificationStatus.Delivered,
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NotificationRequests.Add(req);

        var del = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            RequestId = req.Id,
            UserId = userId,
            Status = status,
            Channel = "signalr",
            DeliveredAt = deliveredAt ?? DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NotificationDeliveries.Add(del);
        t.Db.SaveChanges();
        return del;
    }

    [Fact]
    public async Task ReadByTarget_MarksOnlyMatchingDeliveriesOfThatUser()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var me = t.AddUser("100");
        var other = t.AddUser("200");
        var room = Guid.NewGuid();
        var mail = Guid.NewGuid();
        var task = Guid.NewGuid();
        var chat1 = AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "m1", "a");
        var chat2 = AddDelivery(t, c, me.Id, "chat.emergency", NotificationStatus.Delivered, "m2", "b");
        var otherRoom = AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "m3", "c");
        var theirs = AddDelivery(t, c, other.Id, "chat.mention", NotificationStatus.Delivered, "m4", "d");
        var mailD = AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "m5", "e");
        var taskD = AddDelivery(t, c, me.Id, "project.task_assigned", NotificationStatus.Delivered, "m6", "f");
        void Url(NotificationDelivery d, string url) => t.Db.NotificationRequests.Single(r => r.Id == d.RequestId).TargetUrl = url;
        Url(chat1, $"/chat/{room}?jump={Guid.NewGuid()}");
        Url(chat2, $"/chat/{room}");
        Url(otherRoom, $"/chat/{Guid.NewGuid()}");
        Url(theirs, $"/chat/{room}");
        Url(mailD, $"/mail?mailId={mail}");
        Url(taskD, $"/projects/{Guid.NewGuid()}?task={task}");
        t.Db.SaveChanges();

        Assert.Equal(2, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "chat", room, default));
        Assert.Equal(1, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "mail", mail, default));
        Assert.Equal(1, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "task", task, default));
        Assert.Equal(0, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "chat", room, default));
        Assert.Equal(0, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "bogus", room, default));

        await using var v = t.Verify();
        var st = await v.NotificationDeliveries.ToDictionaryAsync(d => d.Id, d => d.Status);
        Assert.Equal(NotificationStatus.Read, st[chat1.Id]);
        Assert.Equal(NotificationStatus.Read, st[chat2.Id]);
        Assert.Equal(NotificationStatus.Delivered, st[otherRoom.Id]);
        Assert.Equal(NotificationStatus.Delivered, st[theirs.Id]);
        Assert.Equal(NotificationStatus.Read, st[mailD.Id]);
        Assert.Equal(NotificationStatus.Read, st[taskD.Id]);
    }

    [Fact]
    public async Task ClassifyEventType_MapsModuleKinds()
    {
        Assert.Equal("chat", NotificationGatewayEndpoints.ClassifyEventType("chat.mention"));
        Assert.Equal("mail", NotificationGatewayEndpoints.ClassifyEventType("mail.new"));
        Assert.Equal("news", NotificationGatewayEndpoints.ClassifyEventType("memo.ack"));
        Assert.Equal("project", NotificationGatewayEndpoints.ClassifyEventType("task.assigned"));
        Assert.Equal("system", NotificationGatewayEndpoints.ClassifyEventType("payroll.ready"));
        Assert.Equal("system", NotificationGatewayEndpoints.ClassifyEventType("UnknownCase"));
    }

    [Fact]
    public async Task ListInbox_ReturnsOnlyMyDeliveries_NewestFirst()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var other = t.AddUser("20140312");
        var c = AddClient(t);

        var old = AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Read, "old", "k1", DateTime.UtcNow.AddDays(-2));
        var newer = AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "newer", "k2", DateTime.UtcNow.AddMinutes(-5));
        // someone else's delivery must not appear
        AddDelivery(t, c, other.Id, "mail.new", NotificationStatus.Delivered, "not-mine", "k3");

        var res = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);

        Assert.Equal(2, res.TotalCount);
        Assert.Equal(1, res.UnreadCount);
        Assert.Equal(newer.RequestId, res.Items[0].Id);   // newest first
        Assert.Equal(old.RequestId, res.Items[1].Id);
        Assert.All(res.Items, i => Assert.NotEqual("not-mine", i.Title));
    }

    [Fact]
    public async Task ListInbox_FiltersByType()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var c = AddClient(t);
        AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "chat-one", "c1");
        AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "mail-one", "m1");
        AddDelivery(t, c, me.Id, "payroll.ready", NotificationStatus.Delivered, "sys-one", "s1");

        var chat = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, "chat", null, 1, 20, default);
        var sys = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, "system", null, 1, 20, default);

        Assert.Single(chat.Items);
        Assert.Equal("chat-one", chat.Items[0].Title);
        Assert.Single(sys.Items);
        Assert.Equal("sys-one", sys.Items[0].Title);
    }

    [Fact]
    public async Task ListInbox_FiltersByStatus()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var c = AddClient(t);
        AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Read, "read-one", "r1");
        AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "unread-one", "u1");
        AddDelivery(t, c, me.Id, "task.assigned", NotificationStatus.Acknowledged, "acked-one", "a1");

        var unread = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "unread", 1, 20, default);
        var read = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "read", 1, 20, default);

        Assert.Single(unread.Items);
        Assert.Equal("unread-one", unread.Items[0].Title);
        Assert.Equal(2, read.Items.Count);
        Assert.Contains(read.Items, i => i.Title == "read-one");
        Assert.Contains(read.Items, i => i.Title == "acked-one");
    }

    [Fact]
    public async Task ListInbox_Paginates()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var c = AddClient(t);
        for (var i = 0; i < 5; i++)
            AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, $"m{i}", $"k{i}", DateTime.UtcNow.AddMinutes(-i));

        var p1 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 2, default);
        var p2 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 2, 2, default);

        Assert.Equal(5, p1.TotalCount);
        Assert.Equal(2, p1.Items.Count);
        Assert.Equal(2, p2.Items.Count);
        Assert.Equal("m0", p1.Items[0].Title); // newest first
        Assert.Equal("m2", p2.Items[0].Title);
    }

    [Fact]
    public async Task MarkAllRead_MarksOnlyMyUnread_ReturnsCount()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var other = t.AddUser("20140312");
        var c = AddClient(t);
        AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "unread-1", "u1");
        AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Read, "already-read", "r1");
        AddDelivery(t, c, me.Id, "task.assigned", NotificationStatus.Delivered, "unread-2", "u2");
        AddDelivery(t, c, other.Id, "chat.mention", NotificationStatus.Delivered, "other-unread", "u3");

        var updated = await Svc(t).MarkAllReadAsync(me.Id, default);

        Assert.Equal(2, updated); // only my 2 unread; already-read + other's delivery untouched

        var res = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);
        Assert.Equal(0, res.UnreadCount); // all my deliveries now read
        Assert.Equal(3, res.TotalCount);

        // other user's delivery is still unread
        var otherRes = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), other.Id, null, null, 1, 20, default);
        Assert.Equal(1, otherRes.UnreadCount);
    }

    [Fact]
    public async Task DeleteAndBulkActions_ManageUserDeliveries()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080116");
        var c = AddClient(t);
        var d1 = AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "del-1", "d1");
        var d2 = AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "del-2", "d2");
        var d3 = AddDelivery(t, c, me.Id, "task.assigned", NotificationStatus.Delivered, "del-3", "d3");
        await t.Db.SaveChangesAsync();

        // Bulk read d1 & d2
        var toRead = await t.Db.NotificationDeliveries.Where(d => d.UserId == me.Id && (d.RequestId == d1.RequestId || d.RequestId == d2.RequestId)).ToListAsync();
        foreach (var d in toRead) d.Status = NotificationStatus.Read;
        await t.Db.SaveChangesAsync();

        var res = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);
        Assert.Equal(1, res.UnreadCount);

        // Delete d3
        var del = await t.Db.NotificationDeliveries.FirstOrDefaultAsync(d => d.RequestId == d3.RequestId && d.UserId == me.Id);
        t.Db.NotificationDeliveries.Remove(del!);
        await t.Db.SaveChangesAsync();

        res = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);
        Assert.Equal(2, res.TotalCount);
        Assert.DoesNotContain(res.Items, x => x.Id == d3.RequestId);
    }
}
