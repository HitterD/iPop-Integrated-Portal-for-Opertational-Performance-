using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// A mention notification links the right room and is created once even when the worker retries.
public class MentionNotificationTests
{
    private sealed class NoBroadcast : INotificationRealtimeBroadcaster
    {
        public int Calls;
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Retry_of_the_same_mention_creates_one_notification_linked_by_room_id()
    {
        using var t = new TestDb();
        var b = t.AddUser("B");
        // Two rooms share a name: a lookup by name would link the wrong one.
        var other = new ChatRoom { Id = Guid.NewGuid(), Type = "group", Name = "Team", IsActive = true, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
        var room = new ChatRoom { Id = Guid.NewGuid(), Type = "group", Name = "Team", IsActive = true, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
        t.Db.ChatRooms.AddRange(other, room);
        t.Db.SaveChanges();
        var messageId = Guid.NewGuid();
        var broadcast = new NoBroadcast();

        for (var attempt = 0; attempt < 2; attempt++)
            await new NotificationGateway(NullLogger<NotificationGateway>.Instance, t.Verify(), broadcast)
                .SendMentionNotificationAsync(b.Id, "A", "Team", "hi @B", false, default, messageId, room.Id);

        var db = t.Verify();
        var request = Assert.Single(db.NotificationRequests.Where(r => r.EventType == "chat.mention"));
        Assert.Equal($"/chat/{room.Id}?jump={messageId}", request.TargetUrl);
        Assert.Single(db.NotificationDeliveries.Where(d => d.RequestId == request.Id && d.UserId == b.Id));
    }
}
