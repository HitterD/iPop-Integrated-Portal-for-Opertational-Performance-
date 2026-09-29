using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/audit", async (string? q, string? actor, string? action, string? entity, string? type,
            DateTime? from, DateTime? to, int? page, int? pageSize, string? user, IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var query = BuildAuditQuery(db, q, actor, action, entity, type, from, to, user);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip((p - 1) * size).Take(size).Select(x => new AuditRow(
                    x.Id, x.ActorNik, x.Action, x.Entity, x.EntityId, x.DetailJson, x.IpAddress, x.CreatedAt, null, null))
                .ToListAsync(ct);
            return Results.Ok(new { total, page = p, pageSize = size, items = await WithNames(db, items, ct) });
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/admin/audit/export.csv", async (string? q, string? actor, string? action, string? entity, string? type,
            DateTime? from, DateTime? to, IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var callerNik = http.User.FindFirst("nik")?.Value;
            var query = BuildAuditQuery(db, q, actor, action, entity, type, from, to);
            var logs = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(5000).ToListAsync(ct);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Timestamp;Actor;Action;Entity;EntityId;IP Address");
            foreach (var r in logs)
            {
                sb.AppendLine(string.Join(';',
                    r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + "Z",
                    DirectoryEndpoints.CsvField(r.ActorNik ?? "system"),
                    DirectoryEndpoints.CsvField(r.Action),
                    DirectoryEndpoints.CsvField(r.Entity ?? ""),
                    DirectoryEndpoints.CsvField(r.EntityId ?? ""),
                    DirectoryEndpoints.CsvField(r.IpAddress ?? "")));
            }

            db.AuditLogs.Add(new AuditLog
            {
                ActorNik = callerNik,
                Action = "audit.export_csv",
                Entity = "AuditLog",
                EntityId = $"type={type ?? "all"};n={logs.Count}",
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", "audit_trail.csv");
        }).RequireAuthorization("AdminOnly");

        return app;
    }

    // Resolve actor and target-user NIKs to names so the UI can write "Ana signed Budi out".
    internal static async Task<List<AuditRow>> WithNames(IPopDbContext db, List<AuditRow> rows, CancellationToken ct)
    {
        var niks = rows.Select(r => r.ActorNik)
            .Concat(rows.Where(r => r.Entity == "User").Select(r => r.EntityId))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        if (niks.Count == 0) return rows;
        var names = await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik))
            .Select(u => new { u.Nik, u.Name }).ToDictionaryAsync(u => u.Nik, u => u.Name, ct);
        return rows.Select(r => r with
        {
            ActorName = r.ActorNik is { } a && names.TryGetValue(a, out var an) ? an : null,
            EntityName = r.Entity == "User" && r.EntityId is { } e && names.TryGetValue(e, out var en) ? en : null,
        }).ToList();
    }

    public static IQueryable<AuditLog> BuildAuditQuery(IPopDbContext db, string? q, string? actor, string? action,
        string? entity, string? type, DateTime? from, DateTime? to, string? user = null)
    {
        var query = db.AuditLogs.AsNoTracking();
        // user = everything a person did or had done to their account (exact NIK, unlike q).
        if (!string.IsNullOrWhiteSpace(user))
        {
            var u = user.Trim();
            query = query.Where(x => x.ActorNik == u || (x.Entity == "User" && x.EntityId == u));
        }
        if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(x => x.ActorNik == actor.Trim());
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action.Trim());
        if (!string.IsNullOrWhiteSpace(entity)) query = query.Where(x => x.Entity == entity.Trim());
        if (!string.IsNullOrWhiteSpace(type) && !type.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var t = type.Trim().ToLowerInvariant();
            query = t switch
            {
                "security" => query.Where(x => x.Action.StartsWith("admin.session") || x.Action.StartsWith("auth") || x.Action.StartsWith("security")),
                "user" => query.Where(x => x.Action.StartsWith("admin.user") || x.Action.StartsWith("users")),
                "sync" => query.Where(x => x.Action.StartsWith("hris") || x.Action.StartsWith("sync")),
                "org" => query.Where(x => x.Action.StartsWith("admin.org") || x.Action.StartsWith("org")),
                "settings" => query.Where(x => x.Action.StartsWith("settings")),
                _ => query
            };
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(x => x.Action.ToLower().Contains(term)
                || (x.ActorNik != null && x.ActorNik.ToLower().Contains(term))
                || (x.Entity != null && x.Entity.ToLower().Contains(term))
                || (x.EntityId != null && x.EntityId.ToLower().Contains(term)));
        }
        if (from is not null) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to is not null) query = query.Where(x => x.CreatedAt <= to.Value);
        return query;
    }

    public record AuditRow(Guid Id, string? ActorNik, string Action, string? Entity,
        string? EntityId, string? DetailJson, string? IpAddress, DateTime CreatedAt,
        string? ActorName = null, string? EntityName = null);
}
