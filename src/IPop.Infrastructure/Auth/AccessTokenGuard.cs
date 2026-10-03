using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Auth;

public enum AccessCheck { Ok, MustChangePassword, Rejected }

/// Runs on every authenticated request (JwtBearer OnTokenValidated). A signed, unexpired JWT is not
/// enough: the account must still be active and unlocked, and the login the token came from must
/// still have a live session — otherwise lock, reset, deactivate and "sign out this device" would
/// only bite when the 15-minute token ran out.
public static class AccessTokenGuard
{
    public const string SessionClaim = "sfam";
    /// Added to the principal while the account is on a default/reset password.
    public const string PasswordChangeClaim = "pwd_change";

    public static async Task<AccessCheck> CheckAsync(IPopDbContext db, Guid userId, Guid? family, DateTime now, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.LockedUntil, u.MustChangePassword })
            .FirstOrDefaultAsync(ct);
        if (user is null || !user.IsActive || user.LockedUntil > now) return AccessCheck.Rejected;

        // Tokens minted before families existed carry no claim; they age out within 15 minutes.
        if (family is { } f && !await db.AuthSessions.AnyAsync(
                s => s.FamilyId == f && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now, ct))
            return AccessCheck.Rejected;

        return user.MustChangePassword ? AccessCheck.MustChangePassword : AccessCheck.Ok;
    }

    /// While a password change is pending only the auth endpoints answer; everything else
    /// (APIs and hubs) is refused, so the /set-password redirect is not just a client nicety.
    public static bool AllowedDuringPasswordChange(string path)
        => path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/auth", StringComparison.OrdinalIgnoreCase);
}
