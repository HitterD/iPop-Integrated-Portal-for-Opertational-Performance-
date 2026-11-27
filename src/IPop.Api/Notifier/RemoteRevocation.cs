using IPop.Application.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// The company policy holds for a session's whole life, not just its connect (review PR #5): every /join (first join
/// and each rejoin) checks it again, and switching Remote off, blocking a helper or excluding a PC ends every running
/// session it touches — prompt-based ones too — on the hub and in LiveKit.
public static class RemoteRevocation
{
    /// Null when the session may (re)join; otherwise why not (the session is then ended by the caller).
    public static async Task<string?> StillAllowedAsync(IPopDbContext db, RemoteSession s, RemotePolicy p, bool helperIsAdmin, CancellationToken ct)
    {
        if (!p.RemoteOn) return "off";
        if (!p.Allows(s.HelperId)) return "blocked";
        if (await db.RemoteHelperBlocks.AsNoTracking().AnyAsync(b => b.UserId == s.HelperId, ct)) return "blocked";
        if (!await IPop.Api.Endpoints.NotifierEndpoints.IsIctOrAdminAsync(db, s.HelperId, helperIsAdmin, ct)) return "blocked";
        var excluded = await db.NotifierDevices.AsNoTracking().Where(d => d.Id == s.DeviceId).Select(d => d.Machine != null && d.Machine.Excluded).FirstOrDefaultAsync(ct);
        return excluded ? "excluded" : null;
    }

    /// Ends running and asking sessions matching <paramref name="which"/> (ended by <paramref name="reason"/>); returns them
    /// so the caller can tell both sides and drop them from LiveKit after saving.
    public static async Task<List<RemoteSession>> EndAsync(IPopDbContext db, System.Linq.Expressions.Expression<Func<RemoteSession, bool>> which, string reason, DateTime now, CancellationToken ct)
    {
        var live = await db.RemoteSessions.Where(s => s.Status == "active" || s.Status == "asking").Where(which).ToListAsync(ct);
        foreach (var s in live) { s.Status = s.Status == "asking" ? "expired" : "ended"; s.EndedAt = now; s.EndedBy = reason; }
        return live;
    }

    /// Every session on a PC (excluded).
    public static Task<List<RemoteSession>> MachineAsync(IPopDbContext db, Guid machineId, DateTime now, CancellationToken ct) =>
        EndAsync(db, s => s.Device.MachineId == machineId, "revoked", now, ct);

    /// Every session of one helper (blocked).
    public static Task<List<RemoteSession>> HelperAsync(IPopDbContext db, Guid helperId, DateTime now, CancellationToken ct) =>
        EndAsync(db, s => s.HelperId == helperId, "revoked", now, ct);

    /// Every session (Remote switched off for the company), or of the helpers the new policy no longer allows.
    public static async Task<List<RemoteSession>> PolicyAsync(IPopDbContext db, RemotePolicy p, DateTime now, CancellationToken ct)
    {
        if (!p.RemoteOn) return await EndAsync(db, s => true, "revoked", now, ct);
        var helpers = await db.RemoteSessions.Where(s => s.Status == "active" || s.Status == "asking").Select(s => s.HelperId).Distinct().ToListAsync(ct);
        var gone = helpers.Where(h => !p.Allows(h)).ToList();
        return gone.Count == 0 ? [] : await EndAsync(db, s => gone.Contains(s.HelperId), "revoked", now, ct);
    }
}
