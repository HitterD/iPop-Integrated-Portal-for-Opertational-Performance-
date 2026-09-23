using IPop.Application.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notifications;

/// <summary>
/// SignalR implementation of INotificationRealtimeBroadcaster: pushes each new
/// notification to the recipient's own connections via hub (Clients.User).
/// The client hooks "notificationReceived" on /hubs/notification to bump its
/// badge and refresh the inbox.
/// </summary>
public class NotificationBroadcaster(Microsoft.AspNetCore.SignalR.IHubContext<NotificationHub> hub)
    : INotificationRealtimeBroadcaster
{
    public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
    {
        if (deliveries.Count == 0) return Task.CompletedTask;
        // Per-recipient fan-out: each user's connections get only their own state.
        return Task.WhenAll(deliveries.Select(d =>
            hub.Clients.User(d.UserId.ToString())
                .SendAsync("notificationReceived", d, ct)));
    }
}
