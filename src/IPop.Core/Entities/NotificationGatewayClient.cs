namespace IPop.Core.Entities;

/// <summary>
/// An internal application (HRIS/ERP/CRM...) registered to send notifications through the
/// Intelligent Notification Gateway. Client auth is header-based (ClientId + Secret);
/// the secret is stored as a hash only — secret rotation replaces the hash.
/// </summary>
public class NotificationGatewayClient
{
    public Guid Id { get; set; }

    /// Opaque identifier the app sends as "X-Client-Id".
    public required string ClientId { get; set; }

    /// Argon2-style hash of the client secret. Never store the plaintext secret.
    public required string SecretHash { get; set; }

    /// CSV of allowed source IPs. Empty/null = restricted to the gateway's own
    /// network policy (stub: v1 accepts any IP when empty, v2 enforces).
    public string? AllowedIps { get; set; }

    /// CSV of event types this client may dispatch. Empty/null = all types.
    /// Enforced at enqueue time against NotificationRequest.EventType.
    public string? AllowedEventTypes { get; set; }

    /// Max requests per second (sliding window). 0 = unlimited (trusted system clients).
    public int RateLimitPerSecond { get; set; }

    /// Max deliveries a single request may fan out to (protects against "All Users" on a huge tenant).
    public int MaxRecipients { get; set; } = 1000;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? SecretRotatedAt { get; set; }

    public ICollection<NotificationRequest> Requests { get; set; } = [];
}
