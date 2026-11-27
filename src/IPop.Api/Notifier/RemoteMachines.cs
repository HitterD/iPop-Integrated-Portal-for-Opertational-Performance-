using System.Security.Cryptography;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// One 9-digit remote ID per physical PC (BIGIS #8, phase 1a). Every person's Notifier on the PC reports the same
/// Windows MachineGuid + computer name, so they all land on one <see cref="RemoteMachine"/>.
///
/// The report comes from the client, so it is never trusted on a name alone (review PR #5): a device joins a PC that
/// already has another person's Notifier only when it reports that PC's MachineGuid too; a name-only report from a
/// second person gets its own machine instead of borrowing the PC's ID. Conflicting reports are flagged
/// (<see cref="ConflictsAsync"/>) and Connect refuses an ambiguous PC.
public static class RemoteMachines
{
    /// "{guid}|{NAME}" or "|{NAME}" (a Notifier before 1.8.0 sends no GUID); null when the name is unusable.
    public static string? Key(string? machineGuid, string? computerName)
    {
        var name = (computerName ?? "").Trim().ToUpperInvariant();
        if (name.Length is 0 or > 64) return null;
        var guid = Guid.TryParse(machineGuid, out var g) && g != Guid.Empty ? g.ToString("D") : "";
        return guid + "|" + name;
    }

    /// "482913576" → "482 913 576".
    public static string Format(string connectId) =>
        connectId.Length == 9 ? $"{connectId[..3]} {connectId[3..6]} {connectId[6..]}" : connectId;

    /// "482 913 576" / "482-913-576" / "482913576" → "482913576"; null unless exactly 9 digits.
    public static string? ParseId(string? text)
    {
        var t = (text ?? "").Trim();
        if (!t.All(c => char.IsAsciiDigit(c) || c is ' ' or '-')) return null;
        var digits = new string(t.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 9 ? digits : null;
    }

    /// The PC for this report, created with a fresh random ID the first time.
    ///   • exact key (GUID + name) → that PC (the GUID is the strong sign; the name must match too).
    ///   • GUID report, a "|NAME" row exists → adopted (key upgraded, ID kept) only when it is the one such row and
    ///     every device already on it belongs to <paramref name="userId"/> — never another person's name-only PC.
    ///   • name-only report → an existing PC with that name only when exactly one exists and it holds no other
    ///     person's device; otherwise a separate "|NAME#user" machine of its own.
    public static async Task<RemoteMachine?> EnsureAsync(IPopDbContext db, Guid userId, string? machineGuid, string? computerName, DateTime now, CancellationToken ct)
    {
        var key = Key(machineGuid, computerName);
        if (key is null) return null;
        var name = key[(key.IndexOf('|') + 1)..];
        var nameOnly = "|" + name;
        var row = await db.RemoteMachines.FirstOrDefaultAsync(m => m.MachineKey == key, ct);
        if (row is not null && (key != nameOnly || await OnlyMineAsync(db, row.Id, userId, ct))) return row;
        if (row is null && key != nameOnly)
        {
            var old = await db.RemoteMachines.Where(m => m.MachineKey == nameOnly).Take(2).ToListAsync(ct);
            if (old.Count == 1 && await OnlyMineAsync(db, old[0].Id, userId, ct)) { old[0].MachineKey = key; return old[0]; }
        }
        else if (key == nameOnly)
        {
            var same = await db.RemoteMachines.Where(m => m.ComputerName == name).Take(2).ToListAsync(ct);
            if (same.Count == 1 && await OnlyMineAsync(db, same[0].Id, userId, ct)) return same[0];
            if (same.Count > 0 || row is not null)
            {
                // Someone else's PC carries this name: a machine of this person's own, never the shared ID.
                key = nameOnly + "#" + userId.ToString("N");
                row = await db.RemoteMachines.FirstOrDefaultAsync(m => m.MachineKey == key, ct);
                if (row is not null) return row;
            }
        }
        row = new RemoteMachine { Id = Guid.NewGuid(), MachineKey = key, ComputerName = name, ConnectId = await NewIdAsync(db, ct), CreatedAt = now };
        db.RemoteMachines.Add(row);
        return row;
    }

    /// No device of another person is linked to the machine.
    private static async Task<bool> OnlyMineAsync(IPopDbContext db, Guid machineId, Guid userId, CancellationToken ct) =>
        !await db.NotifierDevices.AnyAsync(d => d.MachineId == machineId && d.UserId != userId, ct);

    /// 9 random digits (first one not 0), not used by another PC. Random, so IDs never reveal order or count.
    public static async Task<string> NewIdAsync(IPopDbContext db, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var id = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
            if (db.RemoteMachines.Local.Any(m => m.ConnectId == id)) continue;
            if (!await db.RemoteMachines.AnyAsync(m => m.ConnectId == id, ct)) return id;
        }
        throw new InvalidOperationException("No free remote ID.");
    }

    /// Links the device to its PC and carries the pre-1.8.0 per-device unattended switch over to the PC until an
    /// Administrator first touches the PC's access (SetById). The switch alone grants nothing: unattended access also
    /// needs a password. The old device flag is left as it was, so rolling the migration back loses nothing.
    public static async Task LinkAsync(IPopDbContext db, NotifierDevice device, string? machineGuid, DateTime now, CancellationToken ct)
    {
        var m = await EnsureAsync(db, device.UserId, machineGuid, device.MachineName, now, ct);
        if (m is null) return;
        device.MachineId = m.Id;
        if (device.Unattended && m.SetById is null) m.Unattended = true;
    }

    /// Machines whose devices disagree: the same PC name with a different MachineGuid elsewhere (a cloned or spoofed
    /// report), or Notifiers on one PC seen from different addresses within the online window. Shown as a warning in
    /// Devices; Connect refuses such a PC by name (the ID still works, the Admin decides).
    public static async Task<HashSet<Guid>> ConflictsAsync(IPopDbContext db, DateTime now, CancellationToken ct)
    {
        var since = now - NotifierDevice.OnlineWindow;
        var machines = await db.RemoteMachines.AsNoTracking().Select(m => new { m.Id, m.ComputerName, m.MachineKey }).ToListAsync(ct);
        var flagged = machines.GroupBy(m => m.ComputerName).Where(g => g.Count() > 1).SelectMany(g => g.Select(m => m.Id)).ToHashSet();
        var online = await db.NotifierDevices.AsNoTracking().Where(d => d.MachineId != null && d.LastSeenAt >= since)
            .Select(d => new { MachineId = d.MachineId!.Value, d.RemoteIp }).ToListAsync(ct);
        foreach (var g in online.GroupBy(d => d.MachineId))
            if (g.Select(d => d.RemoteIp ?? "").Distinct().Count() > 1) flagged.Add(g.Key);
        return flagged;
    }
}
