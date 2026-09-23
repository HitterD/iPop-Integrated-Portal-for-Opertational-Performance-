namespace IPop.Application.Notifications;

/// <summary>
/// Pushes freshly-delivered notifications to online recipients over SignalR.
/// Implemented in IPop.Api (hub context); the gateway service in Infrastructure
/// depends on this interface so it never touches Hub types directly.
/// </summary>
public interface INotificationRealtimeBroadcaster
{
    /// Sends "notificationReceived" to each recipient's SignalR connection(s).
    /// Callers pass the deliveries that just became Delivered/Read; the payload is
    /// the per-recipient state so the client can update its badge/list immediately.
    Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default);
}
