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

    public sealed record Outcome(Check Check, int TriesLeft, DateTime? LockedUntil);

    /// Ok resets the counter; Wrong counts and locks on the fifth; Locked while the lock runs (the password is not
    /// even checked then, so guessing during a lock teaches nothing).
    /// Every change is one conditional UPDATE on the row (review PR #5): parallel wrong guesses each count, and only
    /// one of them sets the lock — no read-increment-save that two requests could both base on the same count.
    public static async Task<Outcome> VerifyAsync(IPopDbContext db, Guid machineId, string? password, IPasswordHasher hasher, DateTime now, CancellationToken ct)
    {
        var m = await db.RemoteMachines.AsNoTracking().Where(x => x.Id == machineId)
            .Select(x => new { x.Unattended, x.PasswordHash, x.LockedUntil }).FirstOrDefaultAsync(ct);
        if (m is null || !m.Unattended || m.PasswordHash is null) return new(Check.NotSet, 0, null);
        if (m.LockedUntil > now) return new(Check.Locked, 0, m.LockedUntil);
        // An expired lock starts a fresh count.
        await db.RemoteMachines.Where(x => x.Id == machineId && x.LockedUntil != null && x.LockedUntil <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.FailedCount, 0), ct);
        var right = !string.IsNullOrEmpty(password) && password.Length <= 200 && hasher.Verify(password, m.PasswordHash);
        if (right)
        {
            // Only if nobody locked it meanwhile.
            var reset = await db.RemoteMachines.Where(x => x.Id == machineId && x.LockedUntil == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedCount, 0), ct);
            return reset == 1 ? new(Check.Ok, RemoteMachine.MaxFailedPasswords, null) : await LockedAsync(db, machineId, ct);
        }
        var counted = await db.RemoteMachines.Where(x => x.Id == machineId && x.LockedUntil == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedCount, x => x.FailedCount + 1), ct);
        if (counted == 0) return await LockedAsync(db, machineId, ct);
        var until = now + RemoteMachine.LockFor;
        var locked = await db.RemoteMachines.Where(x => x.Id == machineId && x.LockedUntil == null && x.FailedCount >= RemoteMachine.MaxFailedPasswords)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, until).SetProperty(x => x.FailedCount, 0), ct);
        if (locked == 1) return new(Check.Locked, 0, until);
        var after = await db.RemoteMachines.AsNoTracking().Where(x => x.Id == machineId).Select(x => new { x.FailedCount, x.LockedUntil }).FirstAsync(ct);
        return after.LockedUntil > now ? new(Check.Locked, 0, after.LockedUntil)
            : new(Check.Wrong, Math.Max(0, RemoteMachine.MaxFailedPasswords - after.FailedCount), null);
    }

    private static async Task<Outcome> LockedAsync(IPopDbContext db, Guid machineId, CancellationToken ct) =>
        new(Check.Locked, 0, await db.RemoteMachines.AsNoTracking().Where(x => x.Id == machineId).Select(x => x.LockedUntil).FirstOrDefaultAsync(ct));

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
