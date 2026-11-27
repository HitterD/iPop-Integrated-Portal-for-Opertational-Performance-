using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// "Connect" in the Remote window (BIGIS #8): a 9-digit ID, a computer name or a person's name → the PC's Notifier to
/// ask. A PC can have several people's Notifiers (one per Windows profile); the one seen last is the one in front.
public static class RemoteConnect
{
    /// Why a connect can't go ahead; <see cref="Status"/> is the HTTP status the viewer reads with <see cref="Code"/>.
    public sealed record Problem(int Status, string Code, string Message, string? Machine = null, string? User = null,
        string? RemoteId = null, DateTime? LastSeenAt = null, int? TriesLeft = null, DateTime? LockedUntil = null)
    {
        public object Body => new { code = Code, message = Message, machine = Machine, user = User, remoteId = RemoteId,
            lastSeenAt = LastSeenAt, triesLeft = TriesLeft, lockedUntil = LockedUntil };
    }

    /// The device to ask (online, newest) and its PC; or the problem to show. Exactly one of the two is set.
    public static async Task<(NotifierDevice? Device, Problem? Problem)> ResolveAsync(IPopDbContext db, Guid? deviceId, string? target,
        DateTime now, CancellationToken ct)
    {
        var since = now - NotifierDevice.OnlineWindow;
        List<NotifierDevice> candidates;
        if (deviceId is { } id)
        {
            candidates = await db.NotifierDevices.Include(d => d.User).Include(d => d.Machine).Where(d => d.Id == id).ToListAsync(ct);
        }
        else
        {
            var t = (target ?? "").Trim();
            if (t.Length is 0 or > 80) return (null, new Problem(400, "unknown", "Enter a remote ID or computer name."));
            var digits = RemoteMachines.ParseId(t);
            var upper = t.ToUpperInvariant();
            var q = db.NotifierDevices.Include(d => d.User).Include(d => d.Machine).AsQueryable();
            if (digits is not null) q = q.Where(d => d.Machine != null && d.Machine.ConnectId == digits);
            else q = q.Where(d => d.MachineName.ToUpper() == upper);
            candidates = await q.ToListAsync(ct);
            // A person's name: their PCs, if it names exactly one person.
            if (candidates.Count == 0 && digits is null)
            {
                var people = await db.Users.Where(u => u.IsActive && u.Name.ToUpper() == upper).Select(u => u.Id).Take(2).ToListAsync(ct);
                if (people.Count == 1)
                    candidates = await db.NotifierDevices.Include(d => d.User).Include(d => d.Machine).Where(d => d.UserId == people[0]).ToListAsync(ct);
            }
            if (candidates.Count == 0)
                return (null, new Problem(404, "unknown", digits is not null ? $"No PC has the ID {RemoteMachines.Format(digits)}." : $"No PC called \"{t}\" runs the iPop Notifier."));
            // Several PCs of one person: only when exactly one is on the same machine (an ID or a computer name).
            if (candidates.Select(d => d.MachineId ?? d.Id).Distinct().Count() > 1)
            {
                var online = candidates.Where(d => d.LastSeenAt >= since).ToList();
                if (online.Select(d => d.MachineId ?? d.Id).Distinct().Count() != 1)
                    return (null, new Problem(409, "ambiguous", $"\"{t}\" matches more than one PC. Use the remote ID."));
                candidates = online;
            }
        }
        var pick = candidates.OrderByDescending(d => d.LastSeenAt).FirstOrDefault();
        if (pick is null) return (null, new Problem(404, "unknown", "That computer isn't known."));
        if (pick.LastSeenAt < since)
            return (null, new Problem(409, "offline", $"{pick.MachineName} is offline", pick.MachineName, pick.User.Name, pick.Machine?.ConnectId, pick.LastSeenAt));
        if (await InconsistentAsync(db, pick, since, ct) is { } why)
            return (null, new Problem(409, "conflict", why, pick.MachineName, pick.User.Name, pick.Machine?.ConnectId));
        return (pick, null);
    }

    /// The Notifier picked for a PC must agree with the PC (review PR #5, spoofing): same computer name as the
    /// machine, and every Notifier online on that machine reports from the same address. Otherwise a second report
    /// may be pretending to be this PC, so nobody is asked until an Administrator looks at Devices.
    public static async Task<string?> InconsistentAsync(IPopDbContext db, NotifierDevice pick, DateTime since, CancellationToken ct)
    {
        if (pick.Machine is not { } m) return null;
        if (!string.Equals(pick.MachineName.Trim(), m.ComputerName, StringComparison.OrdinalIgnoreCase))
            return $"{pick.MachineName} doesn't match the PC registered for this ID. Check it in Devices.";
        var ips = await db.NotifierDevices.AsNoTracking().Where(d => d.MachineId == m.Id && d.LastSeenAt >= since)
            .Select(d => d.RemoteIp ?? "").Distinct().Take(3).ToListAsync(ct);
        return ips.Count > 1 ? $"{m.ComputerName} is reported from more than one address. Check it in Devices before connecting." : null;
    }
}
