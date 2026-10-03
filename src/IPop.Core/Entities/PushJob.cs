namespace IPop.Core.Entities;

/// Durable Web Push intent (outbox row). Holds references — a chat message, a gateway request,
/// a call, or a test — not the rendered toast. The worker expands it once into one
/// <see cref="PushDelivery"/> per browser and rebuilds the authorized payload from current data
/// on every send attempt (membership, mutes, preview privacy and call state are re-checked).
public class PushJob
{
    public const int MaxExpandAttempts = 5;

    public Guid Id { get; set; }
    /// "chat" (RefId = message) | "notif" (RefId = NotificationRequest) | "call" (RefId = call) | "test".
    public required string Kind { get; set; }
    public Guid? RefId { get; set; }
    /// Comma-separated recipient ids for "notif"/"test" (already filtered by the producer).
    public string? UserIds { get; set; }
    public string? EventType { get; set; }
    public string? Priority { get; set; }
    /// Toast title override for gateway mentions ("Budi mentioned you · Room"); body comes from RefId.
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }
    /// Not before: retry backoff, and the claim lease while a worker expands it.
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ExpandedAt { get; set; }
    /// Sanitized code only (never an exception message).
    public string? LastError { get; set; }

    public ICollection<PushDelivery> Deliveries { get; set; } = [];
}

/// One intent to one browser subscription. Retried on its own; once "accepted" it is never sent
/// again. "accepted" means the push service took it — not that Windows displayed it.
public class PushDelivery
{
    public const int MaxAttempts = 6;

    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public PushJob Job { get; set; } = default!;
    /// Soft reference: the subscription may be deleted (gone/unsubscribed) while history remains.
    public Guid SubscriptionId { get; set; }
    /// The authorized recipient; the subscription must still belong to this user at send time.
    public Guid UserId { get; set; }
    /// Payload kind: "chat" | "notif" | "call" | "close" | "test".
    public required string Kind { get; set; }
    /// Calls: ring start ticks, so a ring/close is queued once per ring (re-invite rings again).
    public string? Key { get; set; }
    /// "fcm" | "mozilla" | "wns" | "apple" | "other" — diagnostics only.
    public string Provider { get; set; } = "other";
    /// queued | retrying | sending | accepted | gone | auth-error | rejected | expired | suppressed | failed.
    public required string Status { get; set; }
    public int Attempts { get; set; }
    /// Due time; while "sending" it is the claim lease.
    public DateTime NextAttemptAt { get; set; }
    /// Original event deadline (chat TTL, call ring timeout); retries never extend it.
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    /// Sanitized result code (rate-limited, server-error, network, timeout, gone, auth-401, ...).
    public string? Code { get; set; }
}
