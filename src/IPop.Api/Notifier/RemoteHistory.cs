using System.Text;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// One row of Session history (BIGIS #8 v3): a session, or an Admin change to a PC ("cfg").
public record RemoteHistoryRow(DateTime At, string Who, Guid? WhoId, string Pc, string? Ua, int? Seconds, string Mode, string Result, string? Files, string? What);

/// Session history in the Remote window: who, which PC, when, how long, result, files; Admin changes in between.
/// ICT people see only their own sessions; Admins see everyone (and the change rows).
public static class RemoteHistory
{
    /// Result names as the window shows them. "cfg" = an Administrator changed the PC's access.
    public static readonly Dictionary<string, string> Results = new()
    {
        ["done"] = "Done", ["declined"] = "Declined", ["expired"] = "No answer", ["denied"] = "Wrong password",
        ["locked"] = "Locked", ["dropped"] = "Connection lost", ["cfg"] = "Settings changed",
    };

    /// done | declined | expired | denied | locked | dropped for a session row.
    public static string ResultOf(RemoteSession s) => s.Status switch
    {
        "ended" when s.EndedBy is "lost" or "timeout" or "error" => "dropped",
        "ended" => "done",
        "active" => "done",
        var x => x,
    };

    public static async Task<List<RemoteHistoryRow>> ListAsync(IPopDbContext db, Guid? person, bool withChanges, string? result, string? q, CancellationToken ct)
    {
        var sessions = db.RemoteSessions.AsNoTracking().Where(s => s.Status != "asking");
        if (person is { } p) sessions = sessions.Where(s => s.HelperId == p);
        var list = (await sessions.OrderByDescending(s => s.RequestedAt).Take(1000)
            .Select(s => new { s, Helper = s.Helper.Name, Pc = s.Device.Machine != null && s.Device.Machine.Alias != null ? s.Device.Machine.Alias : s.Device.MachineName })
            .ToListAsync(ct))
            .Select(x => new RemoteHistoryRow(x.s.RequestedAt, x.Helper, x.s.HelperId, x.Pc, x.s.Unattended ? "UA" : null,
                x.s.StartedAt is { } st ? (int)((x.s.EndedAt ?? DateTime.UtcNow) - st).TotalSeconds : null, x.s.Mode, ResultOf(x.s), x.s.Files, null)).ToList();
        if (withChanges && person is null)
        {
            var changes = await db.Set<RemoteAuditEntry>().AsNoTracking().OrderByDescending(a => a.At).Take(500)
                .Join(db.RemoteMachines, a => a.MachineId, m => m.Id, (a, m) => new { a, Pc = m.Alias ?? m.ComputerName })
                .ToListAsync(ct);
            var names = await db.Users.AsNoTracking().Where(u => changes.Select(c => c.a.ActorId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
            list.AddRange(changes.Select(c => new RemoteHistoryRow(c.a.At, c.a.ActorId is { } id && names.TryGetValue(id, out var n) ? n : "Administrator",
                c.a.ActorId, c.Pc, null, null, "control", "cfg", null, c.a.What)));
        }
        if (!string.IsNullOrWhiteSpace(result) && result != "all") list = list.Where(r => r.Result == result).ToList();
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(r => (r.Pc + " " + r.Who).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return list.OrderByDescending(r => r.At).Take(1000).ToList();
    }

    /// A cell Excel would run as a formula (=, +, -, @, tab, CR first) gets a leading apostrophe: shown as text.
    public static string Cell(string v) => v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + v : v;

    public static byte[] Csv(IEnumerable<RemoteHistoryRow> rows)
    {
        var sb = new StringBuilder("When (UTC),ICT,PC,Unattended,Length (s),Mode,Result,Files,Change\r\n");
        static string C(string? v) => v is null ? "" : "\"" + Cell(v).Replace("\"", "\"\"").Replace("\n", "; ") + "\"";
        foreach (var r in rows)
            sb.Append($"{r.At:yyyy-MM-dd HH:mm:ss},{C(r.Who)},{C(r.Pc)},{(r.Ua is null ? "" : "yes")},{r.Seconds},{r.Mode},{C(Results.GetValueOrDefault(r.Result, r.Result))},{C(r.Files)},{C(r.What)}\r\n");
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }
}
