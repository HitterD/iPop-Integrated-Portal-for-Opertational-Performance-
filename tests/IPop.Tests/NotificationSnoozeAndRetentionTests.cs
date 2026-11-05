using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class NotificationSnoozeAndRetentionTests
{
    private static NotificationGatewayClient AddClient(TestDb t, string clientId = "hr")
        => t.AddNotificationClient(clientId, "h:secret123", null, null, 0, 1000, true);

    private static NotificationDelivery AddDelivery(
        TestDb t, NotificationGatewayClient c, Guid userId,
        string eventType, string status, string title, string idem, DateTime? deliveredAt = null, DateTime? snoozedUntil = null,
        string channel = NotificationChannel.SignalR)
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
        var delivery = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            RequestId = req.Id,
            UserId = userId,
            Status = status,
            Channel = channel,
            DeliveredAt = deliveredAt ?? DateTime.UtcNow,
            SnoozedUntil = snoozedUntil,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NotificationDeliveries.Add(delivery);
        t.Db.SaveChanges();
        return delivery;
    }

    [Fact]
    public async Task ListInboxCore_HidesSnoozedNotifications_FromDefaultView()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var c = AddClient(t);
        var now = DateTime.UtcNow;

        AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Delivered, "Active Notif", "k1", now);
        AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "Snoozed Notif", "k2", now, snoozedUntil: now.AddHours(2));

        var defaultList = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);
        Assert.Single(defaultList.Items);
        Assert.Equal("Active Notif", defaultList.Items[0].Title);
        Assert.Equal(1, defaultList.UnreadCount);

        var snoozedList = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "snoozed", 1, 20, default);
        Assert.Single(snoozedList.Items);
        Assert.Equal("Snoozed Notif", snoozedList.Items[0].Title);
    }

    [Fact]
    public async Task RetentionWorker_PurgesOldReadAndFailedDeliveries()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080115");
        var c = AddClient(t);
        var old = DateTime.UtcNow.AddDays(-95);
        var fresh = DateTime.UtcNow.AddDays(-10);

        AddDelivery(t, c, me.Id, "chat.mention", NotificationStatus.Read, "Old Read", "k1", old);
        AddDelivery(t, c, me.Id, "mail.new", NotificationStatus.Delivered, "Old Unread", "k2", old); // in-app unread kept
        AddDelivery(t, c, me.Id, "news.broadcast", NotificationStatus.Delivered, "Old Push", "k4", old, channel: NotificationChannel.Push); // purged
        AddDelivery(t, c, me.Id, "news.broadcast", NotificationStatus.Delivered, "Fresh Push", "k5", fresh, channel: NotificationChannel.Push);
        AddDelivery(t, c, me.Id, "task.assigned", NotificationStatus.Read, "Fresh Read", "k3", fresh);

        var services = new ServiceCollection();
        services.AddScoped(_ => t.Db);
        var sp = services.BuildServiceProvider();

        var worker = new NotificationRetentionWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotificationRetentionWorker>.Instance);
        var purged = await worker.RunPurgeAsync(default);

        Assert.True(purged >= 1);

        await using var v = t.Verify();
        var remaining = await v.NotificationDeliveries.Include(d => d.Request).ToListAsync();
        Assert.DoesNotContain(remaining, d => d.Request.Title == "Old Read");
        Assert.Contains(remaining, d => d.Request.Title == "Old Unread");
        Assert.DoesNotContain(remaining, d => d.Request.Title == "Old Push");
        Assert.Contains(remaining, d => d.Request.Title == "Fresh Push");
        Assert.Contains(remaining, d => d.Request.Title == "Fresh Read");
    }

    [Fact]
    public async Task RetentionWorker_PurgesOldDeadSessions_ButKeepsLiveOnesAndTheNewestPerUser()
    {
        using var t = new TestDb();
        var me = t.AddUser("20080116");
        var other = t.AddUser("20080117");
        var now = DateTime.UtcNow;
        IPop.Core.Entities.AuthSession S(Guid user, string hash, int createdDaysAgo, int? revokedDaysAgo, int expiresInDays) => new()
        {
            Id = Guid.NewGuid(), UserId = user, RefreshTokenHash = hash, CreatedAt = now.AddDays(-createdDaysAgo),
            RevokedAt = revokedDaysAgo is { } r ? now.AddDays(-r) : null, ExpiresAt = now.AddDays(expiresInDays),
        };
        t.Db.AuthSessions.AddRange(
            S(me.Id, "old-revoked", 60, 59, 30),     // revoked 59 days ago: purged
            S(me.Id, "old-expired", 200, null, -40), // expired 40 days ago: purged
            S(me.Id, "recent-revoked", 10, 9, 80),   // revoked 9 days ago: kept
            S(me.Id, "live", 1, null, 89),           // live: kept
            S(other.Id, "only-row", 300, 100, -10)); // dead, but the user's only row ("last sign-in"): kept
        await t.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => t.Db);
        var sp = services.BuildServiceProvider();
        await new NotificationRetentionWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotificationRetentionWorker>.Instance)
            .RunPurgeAsync(default);

        await using var v = t.Verify();
        Assert.Equal(["live", "only-row", "recent-revoked"], (await v.AuthSessions.Select(s => s.RefreshTokenHash).ToListAsync()).Order().ToArray());
    }
}
