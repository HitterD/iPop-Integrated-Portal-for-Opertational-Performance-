using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Notifications.Push;

/// Bound from the "Push" config section (VAPID keypair, base64url). Missing keys = push off:
/// subscriptions are refused and nothing is sent. The keypair must never change — every
/// browser subscription is tied to the public key it was created with.
public sealed class PushOptions
{
    public string? VapidPublicKey { get; set; }
    public string? VapidPrivateKey { get; set; }
    /// Contact the push services can reach if the sender misbehaves (mailto: or https:).
    public string Subject { get; set; } = "mailto:it@santos.co.id";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);

    public static PushOptions Bind(IConfiguration cfg) =>
        cfg.GetSection("Push").Get<PushOptions>() ?? new PushOptions();
}
