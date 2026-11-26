using System.Security.Cryptography;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// One 9-digit remote ID per physical PC (BIGIS #8, phase 1a). Every person's Notifier on the PC reports the same
/// Windows MachineGuid + computer name, so they all land on one <see cref="RemoteMachine"/>.
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

    /// The PC for this report, created with a fresh random ID the first time. A PC first seen by an older Notifier
    /// ("|NAME") keeps its ID when a 1.8.0 Notifier later reports the GUID: the key is upgraded in place.
    public static async Task<RemoteMachine?> EnsureAsync(IPopDbContext db, string? machineGuid, string? computerName, DateTime now, CancellationToken ct)
    {
        var key = Key(machineGuid, computerName);
        if (key is null) return null;
        var name = key[(key.IndexOf('|') + 1)..];
        var nameOnly = "|" + name;
        var row = await db.RemoteMachines.FirstOrDefaultAsync(m => m.MachineKey == key, ct);
        if (row is not null) return row;
        if (key != nameOnly)
        {
            // Same PC seen before without its GUID: adopt that row (keeps the ID people already copied).
            row = await db.RemoteMachines.FirstOrDefaultAsync(m => m.MachineKey == nameOnly, ct);
            if (row is not null) { row.MachineKey = key; return row; }
        }
        else
        {
            // An older Notifier on a PC a 1.8.0 Notifier already reported: the one machine with that name.
            var same = await db.RemoteMachines.Where(m => m.ComputerName == name).Take(2).ToListAsync(ct);
            if (same.Count == 1) return same[0];
        }
        row = new RemoteMachine { Id = Guid.NewGuid(), MachineKey = key, ComputerName = name, ConnectId = await NewIdAsync(db, ct), CreatedAt = now };
        db.RemoteMachines.Add(row);
        return row;
    }

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
        var m = await EnsureAsync(db, machineGuid, device.MachineName, now, ct);
        if (m is null) return;
        device.MachineId = m.Id;
        if (device.Unattended && m.SetById is null) m.Unattended = true;
    }
}
