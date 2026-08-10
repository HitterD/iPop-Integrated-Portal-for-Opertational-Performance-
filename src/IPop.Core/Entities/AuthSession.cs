namespace IPop.Core.Entities;

public class AuthSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string RefreshTokenHash { get; set; }  // hash only, never the raw token
    public string? DeviceFingerprint { get; set; }          // spec §3.3 — navigator data on web, hardware hash on WPF
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public User User { get; set; } = default!;
}
