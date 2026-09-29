using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Auth;

/// Implements spec §3: local auth, null-hash default-password mechanism,
/// 5-attempt lockout, refresh rotation, global invalidation on password change.
public class AuthWorkflow(IPopDbContext db, IPasswordHasher hasher, ITokenService tokens) : IAuthWorkflow
{
    private const string DefaultPassword = "1234";
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(14);

    public async Task<(LoginResponse?, AuthError?, int)> LoginAsync(LoginRequest req, string? ip, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == req.Nik, ct);

        if (user is not null && user.LockedUntil > DateTime.UtcNow)
        {
            var mins = (int)Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
            return (null, new AuthError("locked", $"Too many attempts. Try again in {mins} min."), 423);
        }
        if (user is null || !user.IsActive)
            return (null, new AuthError("invalid_credentials", "Incorrect ID or password."), 401);

        // Null hash → only the default password is accepted (spec §3.3).
        var ok = user.PasswordHash is null
            ? req.Password == DefaultPassword
            : hasher.Verify(req.Password, user.PasswordHash);

        if (!ok)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.Add(LockDuration);
                user.FailedLoginCount = 0;
                await db.SaveChangesAsync(ct);
                await Audit(user.Nik, "auth.locked", "User", user.Nik, ip, ct);
                return (null, new AuthError("locked", "Too many attempts. This account is locked for 15 minutes."), 423);
            }
            await db.SaveChangesAsync(ct);
            var left = MaxFailedAttempts - user.FailedLoginCount;
            return (null, new AuthError("invalid_credentials",
                $"Incorrect ID or password. {left} attempt{(left == 1 ? "" : "s")} left."), 401);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        var roles = await db.UserRoles.Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.Role.Name).ToListAsync(ct);
        var access = tokens.CreateAccessToken(user.Id, user.Nik, roles, out var exp);
        var refresh = tokens.CreateRefreshToken();
        db.AuthSessions.Add(new AuthSession
        {
            UserId = user.Id,
            RefreshTokenHash = tokens.HashRefreshToken(refresh),
            DeviceFingerprint = req.DeviceFingerprint,
            IpAddress = ip,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(RefreshLifetime),
        });
        await db.SaveChangesAsync(ct);
        await Audit(user.Nik, "auth.login", "User", user.Nik, ip, ct);

        return (new LoginResponse(access, refresh, exp, user.MustChangePassword), null, 200);
    }

    public async Task<(AuthError?, int)> ChangePasswordAsync(string nik, ChangePasswordRequest req, string? ip, CancellationToken ct)
    {
        var fields = ValidatePolicy(req.NewPassword, req.ConfirmPassword);
        if (fields.Count > 0)
            return (new AuthError("policy", "Password does not meet the requirements.", fields), 400);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
        if (user is null || !user.IsActive)
            return (new AuthError("invalid", "Account not found or deactivated."), 401);

        user.PasswordHash = hasher.Hash(req.NewPassword);
        user.MustChangePassword = false;

        // Global session invalidation (spec §3.6).
        var now = DateTime.UtcNow;
        await db.AuthSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

        await db.SaveChangesAsync(ct);
        await Audit(user.Nik, "auth.password_changed", "User", user.Nik, ip, ct);
        return (null, 200);
    }

    public async Task<(RefreshResponse?, AuthError?, int)> RefreshAsync(RefreshRequest req, string? ip, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(req.RefreshToken);
        var session = await db.AuthSessions.Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);

        // Spec §3.2: invalid/revoked → 401; valid token but deactivated user → revoke + 401.
        if (session is null || session.RevokedAt is not null || session.ExpiresAt < DateTime.UtcNow)
            return (null, new AuthError("invalid_session", "Session expired. Please sign in again."), 401);
        if (!session.User.IsActive)
        {
            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return (null, new AuthError("deactivated", "Account deactivated."), 401);
        }

        // Rotate: revoke current, issue new. First request wins; reuse of old token gets 401.
        session.RevokedAt = DateTime.UtcNow;
        var roles = await db.UserRoles.Where(ur => ur.UserId == session.UserId)
            .Select(ur => ur.Role.Name).ToListAsync(ct);
        var access = tokens.CreateAccessToken(session.User.Id, session.User.Nik, roles, out var exp);
        var refresh = tokens.CreateRefreshToken();
        db.AuthSessions.Add(new AuthSession
        {
            UserId = session.UserId,
            RefreshTokenHash = tokens.HashRefreshToken(refresh),
            DeviceFingerprint = session.DeviceFingerprint,
            IpAddress = ip ?? session.IpAddress,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(RefreshLifetime),
        });
        await db.SaveChangesAsync(ct);

        return (new RefreshResponse(access, refresh, exp, session.User.MustChangePassword), null, 200);
    }

    public async Task LogoutAsync(LogoutRequest req, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(req.RefreshToken);
        var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is not null)
        {
            // NAS credentials deliberately survive logout: wiping them made every sign-in start
            // with four Files re-logins. They go away on Disconnect or when the NAS rejects them.
            await db.AuthSessions
                .Where(s => s.RefreshTokenHash == hash && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        }
    }

    internal static Dictionary<string, string[]> ValidatePolicy(string pw, string confirm)
    {
        var fields = new Dictionary<string, string[]>();
        var errors = new List<string>();
        if (pw.Length < 8) errors.Add("At least 8 characters.");
        if (!pw.Any(char.IsUpper) || !pw.Any(char.IsDigit)) errors.Add("One uppercase letter and one number.");
        if (pw == DefaultPassword) errors.Add("Must differ from the default password.");
        if (errors.Count > 0) fields["newPassword"] = [.. errors];
        if (pw != confirm) fields["confirmPassword"] = ["Both fields must match."];
        return fields;
    }

    private async Task Audit(string? actor, string action, string entity, string? entityId, string? ip, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = actor, Action = action, Entity = entity, EntityId = entityId,
            IpAddress = ip, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
