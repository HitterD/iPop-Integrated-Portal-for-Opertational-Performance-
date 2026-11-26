using IPop.Application.Notifier;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// Reads and writes the company remote-help policy (AppSettings "remote.policy", audited by DbSettings), and the
/// checks every connect passes before anything else (BIGIS #8 v3): Remote on, the person allowed, the PC not
/// excluded, the hourly connection limit. These run on the server; the Notifier's switches only show them.
public static class RemotePolicyStore
{
    public static async Task<RemotePolicy> GetAsync(ISettings settings, CancellationToken ct) =>
        (await settings.GetAsync<RemotePolicy?>(RemotePolicy.Key, null, ct) ?? new RemotePolicy()).Normalize();

    public static Task SaveAsync(ISettings settings, RemotePolicy policy, string? actorNik, CancellationToken ct) =>
        settings.SetAsync(RemotePolicy.Key, policy.Normalize(), actorNik, ct);

    /// The first rule a connect breaks (code, title, sub for the Remote window's banner), or null.
    public static async Task<(string Code, string Title, string Sub)?> PreflightAsync(IPopDbContext db, RemotePolicy p, Guid helperId,
        RemoteMachine? machine, string? machineName, DateTime now, CancellationToken ct)
    {
        if (!p.RemoteOn) return ("off", "Remote help is switched off for the whole company", "An Administrator turned it off in Settings › Remote.");
        if (!p.Allows(helperId)) return ("blocked", "Remote help is switched off for you", "An Administrator blocked remote help for your account.");
        if (machine is { Excluded: true })
            return ("excluded", $"{machine.Alias ?? machineName ?? machine.ComputerName} is excluded from remote help", "An Administrator excluded this PC. Ask an Administrator if you need access.");
        var since = now - TimeSpan.FromHours(1);
        var used = await db.RemoteSessions.AsNoTracking().CountAsync(s => s.HelperId == helperId && s.RequestedAt > since, ct);
        if (used >= p.RatePerHour)
            return ("rate", "Too many connection attempts", $"You can start {p.RatePerHour} sessions per hour. Try again in a few minutes.");
        return null;
    }

    /// What the user may allow on this PC: the PC's maximum (Admin, Devices), never control in view-only.
    public static RemotePerms PcMax(RemoteMachine? m) => RemotePerms.Parse(m?.AllowedPermissions);

    /// Unattended permissions: the PC's unattended set within its maximum.
    public static RemotePerms Unattended(RemoteMachine? m) => RemotePerms.Parse(m?.UnattendedPermissions).And(PcMax(m));
}
