using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// Unattended access passwords (BIGIS #8): Argon2id hash on the server only, checked when ICT connects; five wrong
/// in a row lock the PC for 15 minutes. Changing or removing the password ends every unattended session on the PC.
public static class RemoteAccess
{
    public const string AllPermissions = "control,clipboard,files";
    private static readonly string[] Known = ["control", "clipboard", "files"];

    public enum Check { Ok, Wrong, Locked, NotSet }

    /// Ok resets the counter; Wrong counts and locks on the fifth; Locked while the lock runs (the password is not
    /// even checked then, so guessing during a lock teaches nothing).
    public static Check Verify(RemoteMachine m, string? password, IPasswordHasher hasher, DateTime now)
    {
        if (!m.UnattendedActive) return Check.NotSet;
        if (m.LockedUntil is { } until && until > now) return Check.Locked;
        if (m.LockedUntil is not null) { m.LockedUntil = null; m.FailedCount = 0; }
        if (!string.IsNullOrEmpty(password) && password.Length <= 200 && hasher.Verify(password, m.PasswordHash!))
        {
            m.FailedCount = 0;
            return Check.Ok;
        }
        m.FailedCount++;
        if (m.FailedCount < RemoteMachine.MaxFailedPasswords) return Check.Wrong;
        m.LockedUntil = now + RemoteMachine.LockFor;
        m.FailedCount = 0;
        return Check.Locked;
    }

    public static int TriesLeft(RemoteMachine m) => Math.Max(0, RemoteMachine.MaxFailedPasswords - m.FailedCount);

    /// Null when fine, else the message for the Administrator.
    public static string? CheckNewPassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < RemoteMachine.MinPasswordLength ? $"Use at least {RemoteMachine.MinPasswordLength} characters."
        : password.Length > 128 ? "Use 128 characters or fewer."
        : null;

    /// New password: hash stored, lock cleared, switch on, running unattended sessions revoked (returned so the
    /// caller can tell both sides).
    public static async Task<List<RemoteSession>> SetPasswordAsync(IPopDbContext db, RemoteMachine m, string password, Guid adminId,
        IPasswordHasher hasher, DateTime now, CancellationToken ct)
    {
        m.PasswordHash = hasher.Hash(password);
        m.PasswordUpdatedAt = now;
        m.SetById = adminId;
        m.Unattended = true;
        m.FailedCount = 0;
        m.LockedUntil = null;
        return await RevokeAsync(db, m.Id, now, ct);
    }

    /// "Remove access": switch off, hash deleted, unattended sessions revoked.
    public static async Task<List<RemoteSession>> RemoveAsync(IPopDbContext db, RemoteMachine m, Guid adminId, DateTime now, CancellationToken ct)
    {
        m.PasswordHash = null;
        m.PasswordUpdatedAt = now;
        m.SetById = adminId;
        m.Unattended = false;
        m.FailedCount = 0;
        m.LockedUntil = null;
        return await RevokeAsync(db, m.Id, now, ct);
    }

    /// Ends the running unattended sessions on every Notifier of the PC (ended by "revoked"). Prompt-based sessions
    /// stay: the user allowed those themselves.
    public static async Task<List<RemoteSession>> RevokeAsync(IPopDbContext db, Guid machineId, DateTime now, CancellationToken ct)
    {
        var live = await db.RemoteSessions
            .Where(s => s.Device.MachineId == machineId && s.Unattended && (s.Status == "active" || s.Status == "asking"))
            .ToListAsync(ct);
        foreach (var s in live) { s.Status = "ended"; s.EndedAt = now; s.EndedBy = "revoked"; }
        return live;
    }

    /// What the user ticked, cleaned: known names only, in a fixed order; view-only sessions never get "control".
    public static string Permissions(IEnumerable<string>? allowed, string mode)
    {
        var set = new HashSet<string>(allowed ?? [], StringComparer.OrdinalIgnoreCase);
        return string.Join(',', Known.Where(k => set.Contains(k) && !(k == "control" && mode == "view")));
    }
}
