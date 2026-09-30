namespace IPop.Core.Entities;

/// A browser's Web Push subscription (one row per browser profile). The push service
/// endpoint is unique; a browser that re-subscribes under another user moves to that user.
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Endpoint { get; set; }
    /// Browser public key (base64url) and auth secret, from PushSubscription.toJSON().keys.
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    /// Human label for the device list, e.g. "Chrome · Windows".
    public string Label { get; set; } = "";
    /// The API base the page used when it subscribed; the service worker posts toast actions there.
    public string? ApiBase { get; set; }
    public DateTime CreatedAt { get; set; }
    /// Refreshed each time the app loads in this browser and re-registers.
    public DateTime LastSeenAt { get; set; }

    public User User { get; set; } = default!;
}
