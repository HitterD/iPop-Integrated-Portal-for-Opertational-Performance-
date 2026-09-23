namespace IPop.Application.Notifications;

/// <summary>
/// Intelligent Notification Gateway — the single API internal apps (HRIS/ERP/CRM) use to
/// deliver notifications centrally. Implemented in IPop.Infrastructure.
/// </summary>
public interface INotificationGatewayService
{
    /// <summary>
    /// Authenticates a client by (ClientId, Secret) + allowed-IP + allowed-event-type checks.
    /// Returns null when the credentials are invalid or the client is inactive.
    /// </summary>
    Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct);

    /// <summary>
    /// Enqueues a request: expands targeting into per-recipient deliveries, assigns the
    /// lifecycle statuses, and (v1 stub) delivers each delivery in-line. Idempotent by
    /// (ClientId, IdempotencyKey) — a resend returns the original request.
    /// </summary>
    Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct);

    /// <summary>
    /// Marks a delivery as read (recipient opened the notification client-side).
    /// </summary>
    Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks every currently-unread delivery for a user as read (the notification-center
    /// "mark all read" action). Returns the number of deliveries updated by this call.
    /// </summary>
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks a delivery as acknowledged (recipient explicitly confirmed receipt).
    /// Acknowledged is terminal — it also implies read.
    /// </summary>
    Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Moves a failed delivery to the dead-letter state ("failed"); a retry before the
    /// attempt budget runs out stays "queued" with next attempt scheduled.
    /// </summary>
    Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Publishes a notification from an internal application module (Mail, Projects, News, System, Chat)
    /// without requiring HTTP/Secret authentication. Automatically provisions or ensures client identity.
    /// </summary>
    Task<NotificationEnqueueResult> PublishInternalAsync(
        string clientSlug,
        string eventType,
        string title,
        string message,
        NotificationTarget target,
        string priority = "normal",
        string? targetUrl = null,
        CancellationToken ct = default);
}
