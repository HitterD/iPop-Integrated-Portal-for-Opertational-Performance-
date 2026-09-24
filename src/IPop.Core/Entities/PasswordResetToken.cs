namespace IPop.Core.Entities;

/// Single-use, short-lived token for the "forgot password" email link. Only the SHA-256
/// hash is stored, the same way AuthSession stores refresh tokens.
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? IpAddress { get; set; }

    public User User { get; set; } = default!;
}
