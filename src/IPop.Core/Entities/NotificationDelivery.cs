namespace IPop.Core.Entities;

/// <summary>
/// One recipient's copy of a <see cref="NotificationRequest"/>. The per-recipient lifecycle:
/// Queued -> Processing -> Delivered -> Read -> Acknowledged, or -> Failed (dead-lettered).
/// Retry counters follow the outbox pattern (stub: delivery is processed in-line, but the
/// columns and state machine exist so the async worker can take over).
/// </summary>
public class NotificationDelivery
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid UserId { get; set; }

    /// "queued" | "processing" | "delivered" | "read" | "acknowledged" | "failed"
    public required string Status { get; set; }

    /// Channel chosen by the stub router: "signalr" (user online) or "push" (FCM, offline) —
    /// decision stub, not a real send.
    public string? Channel { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public NotificationRequest Request { get; set; } = default!;
    public User User { get; set; } = default!;
}
