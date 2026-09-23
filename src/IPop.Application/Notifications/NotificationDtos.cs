namespace IPop.Application.Notifications;

/// Targeting criteria for a notification — exactly one of the target fields is expected to
/// be set (defaults to "all" when none are).
public record NotificationTarget(
    string? EmployeeNik = null,
    Guid? DepartmentId = null,
    string? Role = null,
    bool All = false,
    Guid? UserId = null,
    IReadOnlyList<Guid>? UserIds = null);

/// Inbound submission from an internal app. IdempotencyKey is client-controlled; the gateway
/// de-duplicates on (ClientId, IdempotencyKey).
public record NotificationEnqueueRequest(
    string IdempotencyKey,
    string Priority,          // "high" | "normal" | "low"
    string EventType,
    string Title,
    string Body,
    NotificationTarget Target,
    string? WebhookUrl = null,
    string? TargetUrl = null);

/// Result of EnqueueAsync. When successful, RequestId + the created deliveries are returned.
public record NotificationEnqueueResult(
    bool Success,
    string? Error,
    string? ErrorCode,
    Guid RequestId,
    string Status,
    int DeliveryCount,
    DateTime AcceptedAt);

/// A safe client descriptor (never the secret hash) returned by AuthenticateClientAsync.
public record NotificationGatewayClientInfo(
    Guid Id,
    string ClientId,
    bool IsActive,
    int RateLimitPerSecond,
    int MaxRecipients,
    IReadOnlyList<string> AllowedEventTypes,
    int AllowedEventTypesCount);

/// Per-recipient delivery state returned to callers and mark-read/ack retries.
public record NotificationDeliveryState(
    Guid RequestId,
    Guid UserId,
    string Status,
    string? Channel,
    int AttemptCount,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime? AcknowledgedAt,
    string? Title = null,
    string? Body = null,
    string? EventType = null,
    string? Priority = null,
    string? TargetUrl = null);

/// Status codes surfaced from the service. Use — not free strings — at the API boundary.
public static class NotificationErrors
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string ClientInactive = "client_inactive";
    public const string IpNotAllowed = "ip_not_allowed";
    public const string EventTypeNotAllowed = "event_type_not_allowed";
    public const string InvalidPriority = "invalid_priority";
    public const string RateLimited = "rate_limited";
    public const string TooManyRecipients = "too_many_recipients";
    public const string TargetNotFound = "target_not_found";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string DeliveryNotFound = "delivery_not_found";
    public const string NoTargetSpecified = "no_target_specified";
}

/// Lifecycle statuses (struct literals kept in one place for consistency).
public static class NotificationStatus
{
    public const string Accepted = "accepted";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string Read = "read";
    public const string Acknowledged = "acknowledged";
    public const string Failed = "failed";
}

/// Channel choice from the stub router.
public static class NotificationChannel
{
    public const string SignalR = "signalr";
    public const string Push = "push";
}

