using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Review M2: the inbox filters and counts categories in SQL (NotificationCategorySql). It must agree with
/// NotificationCategory.Of, the C# rule the web page and muting use, for every kind of event type and link.
public class NotificationCategorySqlTests
{
    private static readonly (string Event, string? Url)[] Cases =
    [
        ("chat.mention", "/chat/a"), ("CHAT.Mention", null), ("project.comment_mention", "/projects/x"), ("note.mention", "/notes/1"),
        ("chat.message", "/mail"), ("chat.emergency", null), (" chat.message ", null),
        ("call.missed", null), ("calls.missed", "/calls"), ("meeting.reminder", "/calls?tab=rooms"), ("calendar.invite", "/calendar"),
        ("ticket.sent", "/tickets/a"), ("tickets.assigned", null), ("idesk.reply", "/chat/x"),
        ("mail.new", "/mail?mailId=1"), ("project.task_assigned", null), ("task.due", "/x"),
        ("news.published", "/news/1"), ("memo.mandatory", null),
        ("system.epop_import", "/mail"), ("approval.requested", "/spp/1"), ("admin.notice", null),
        ("chat", null), ("mail", "/x"), ("news", null), ("project", null), ("system", "/chat/r"), ("hrsync", "/tickets/9"),
        ("note.comment", "/notes/1"), ("other.thing", "/idesk/tickets/1#update"), ("x.y", "/calls?tab=scheduled"), ("x.y", "/meeting/1"),
        ("x.y", "/chat/1?m=2"), ("x.y", "/MAIL#a"), ("x.y", "/projects/1?task=2"), ("x.y", "/news"), ("x.y", null), ("x.y", ""),
        ("", "/tickets/1"), ("project", "/chat/1"),
    ];

    [Fact]
    public void Expression_matches_the_csharp_rule_in_memory()
    {
        var of = NotificationCategorySql.Of.Compile();
        foreach (var (ev, url) in Cases)
            Assert.True(NotificationCategory.Of(ev, url) == of(Delivery(ev, url)), $"{ev} {url}: {NotificationCategory.Of(ev, url)} vs {of(Delivery(ev, url))}");
    }

    [Fact]
    public async Task Expression_matches_the_csharp_rule_in_the_database_and_filters_and_counts()
    {
        using var t = new TestDb();
        var me = t.AddUser("CS1");
        var c = t.AddNotificationClient("x", "h", null, null, 0, 1000, true);
        foreach (var (ev, url) in Cases)
        {
            var r = new NotificationRequest { Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = Guid.NewGuid().ToString(), Priority = "normal",
                EventType = ev, Title = ev, Body = "b", TargetUrl = url, Status = "delivered", AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            t.Db.NotificationRequests.Add(r);
            t.Db.NotificationDeliveries.Add(new NotificationDelivery { Id = Guid.NewGuid(), RequestId = r.Id, UserId = me.Id, Status = "delivered",
                DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        }
        t.Db.SaveChanges();

        var inDb = await t.Verify().NotificationDeliveries.AsNoTracking().Select(NotificationCategorySql.Of).ToListAsync();
        var expected = Cases.Select(x => NotificationCategory.Of(x.Event, x.Url)).OrderBy(x => x).ToList();
        Assert.Equal(expected, inDb.OrderBy(x => x).ToList());

        var all = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 100, default);
        foreach (var cat in NotificationCategory.All)
        {
            var n = expected.Count(x => x == cat);
            Assert.Equal(n, all.UnreadByCategory!.GetValueOrDefault(cat));
            var filtered = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 100, default, cat);
            Assert.Equal(n, filtered.TotalCount);
            Assert.All(filtered.Items, i => Assert.Equal(cat, i.Category));
        }
    }

    [Fact]
    public async Task Summary_false_skips_the_counts_but_keeps_the_page()
    {
        using var t = new TestDb();
        var me = t.AddUser("CS2");
        var c = t.AddNotificationClient("x", "h", null, null, 0, 1000, true);
        var r = new NotificationRequest { Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = "k", Priority = "normal",
            EventType = "chat.message", Title = "t", Body = "b", TargetUrl = "/chat/a", Status = "delivered", AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.NotificationRequests.Add(r);
        t.Db.NotificationDeliveries.Add(new NotificationDelivery { Id = Guid.NewGuid(), RequestId = r.Id, UserId = me.Id, Status = "delivered",
            DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var lean = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default, summary: false);
        Assert.Single(lean.Items);
        Assert.Equal(1, lean.TotalCount);
        Assert.Null(lean.UnreadByCategory);
        Assert.Equal(0, lean.UnreadCount);
    }

    private static NotificationDelivery Delivery(string ev, string? url) => new()
    {
        Status = "delivered",
        Request = new NotificationRequest { ClientId = Guid.Empty, IdempotencyKey = "k", Priority = "normal", EventType = ev, Title = "t", Body = "b", TargetUrl = url, Status = "delivered" },
    };
}
