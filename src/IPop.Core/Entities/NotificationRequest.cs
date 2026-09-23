namespace IPop.Core.Entities;

/// <summary>
/// A single notification submitted to the gateway ("send this announcement to the Finance
/// department"). Targeting is expanded into per-recipient <see cref="NotificationDelivery"/>
/// rows. The lifecycle on this row tracks the submission (Accepted -> Queued -> Processing ->
/// Delivered/Failed); per-recipient delivery state lives on NotificationDelivery.
/// </summary>
public class NotificationRequest
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }

    /// Client-supplied idempotency key — unique per client. A resend with the same key
    /// returns the original request instead of creating a duplicate.
    public required string IdempotencyKey { get; set; }

    /// "high" (instant, emergency/compliance) or "bulk" (regular queue). Drives queuing priority.
    public required string Priority { get; set; }

    /// Client-defined event type, e.g. "payroll.ready", "approval.requested".
    /// Must be in the client's AllowedEventTypes when that field is set.
    public required string EventType { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// Routing hint — a client callback URL the gateway POSTs to when delivery
    /// state changes (stub in v1: recorded, no webhook is sent yet).
    public string? WebhookUrl { get; set; }

    /// Target URL deep link for frontend navigation upon clicking the notification.
    public string? TargetUrl { get; set; }

    /// Targeting snapshot, as submitted. May be pushed before expansion.
    /// "employeeId" (single NIK) | "departmentId" | "role" | "all"
    public string? TargetEmployeeNik { get; set; }
    public Guid? TargetDepartmentId { get; set; }
    public string? TargetRole { get; set; }
    public bool TargetAll { get; set; }

    /// Accepted -> Queued -> Processing -> Delivered (all deliveries terminal-delivered)
    /// or Failed (moved to dead-letter). Read/Acknowledged are aggregate output states
    /// observable on the deliveries rather than a request status.
    public required string Status { get; set; }

    public DateTime AcceptedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? FailedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public NotificationGatewayClient Client { get; set; } = default!;
    public ICollection<NotificationDelivery> Deliveries { get; set; } = [];
}
