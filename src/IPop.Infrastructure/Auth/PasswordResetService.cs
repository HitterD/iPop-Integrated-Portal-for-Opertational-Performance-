using System.Net;
using System.Security.Cryptography;
using System.Text;
using IPop.Application.Auth;
using IPop.Application.Email;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Auth;

/// Forgot-password by email (decision 2026-09-23). Tokens live 30 minutes, are single-use,
/// and a new request supersedes older unused ones. At most 3 emails per account per hour,
/// on top of the per-IP rate limit on the endpoint.
public sealed class PasswordResetService(IPopDbContext db, IPasswordHasher hasher, IEmailQueue email) : IPasswordReset
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    internal const int MaxPerHour = 3;

    public async Task RequestAsync(ForgotPasswordRequest req, string resetUrlBase, string? ip, CancellationToken ct)
    {
        var id = req.Identifier?.Trim() ?? "";
        if (id.Length == 0) return;
        var lower = id.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == id || u.Email.ToLower() == lower, ct);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email)) return;

        var now = DateTime.UtcNow;
        var recent = await db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > now.AddHours(-1), ct);
        if (recent >= MaxPerHour) return;

        await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, now), ct);

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, TokenHash = Hash(raw),
            CreatedAt = now, ExpiresAt = now.Add(Lifetime), IpAddress = ip,
        });
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik, Action = "auth.password_reset_requested", Entity = "User",
            EntityId = user.Nik, IpAddress = ip, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        var link = $"{resetUrlBase}?token={Uri.EscapeDataString(raw)}";
        var name = WebUtility.HtmlEncode(user.Name);
        await email.EnqueueAsync(new EmailMessage(
            [user.Email],
            "Reset your IPop password",
            $"<p>Hi {name},</p><p>Someone asked to reset the password for your IPop account ({WebUtility.HtmlEncode(user.Nik)}).</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Choose a new password</a></p>" +
            "<p>This link works once and expires in 30 minutes. If you didn't ask for this, ignore this email — your password stays the same.</p>",
            $"Hi {user.Name},\n\nReset your IPop password: {link}\n\nThis link works once and expires in 30 minutes. If you didn't ask for this, ignore this email."),
            ct);
    }

    public async Task<(AuthError? Err, int Status)> ResetAsync(ResetPasswordRequest req, string? ip, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hash = Hash(req.Token ?? "");
        var token = await db.PasswordResetTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now || !token.User.IsActive)
            return (new AuthError("invalid_token", "This reset link is invalid or has expired. Request a new one."), 400);

        var fields = AuthWorkflow.ValidatePolicy(req.NewPassword ?? "", req.ConfirmPassword ?? "");
        if (fields.Count > 0)
            return (new AuthError("policy", "Password does not meet the requirements.", fields), 400);

        var user = token.User;
        user.PasswordHash = hasher.Hash(req.NewPassword!);
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        token.UsedAt = now;

        // Same global invalidation as a normal password change.
        await db.AuthSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik, Action = "auth.password_reset", Entity = "User",
            EntityId = user.Nik, IpAddress = ip, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return (null, 200);
    }

    internal static string Hash(string raw) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
