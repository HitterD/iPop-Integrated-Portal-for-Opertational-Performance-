using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/audit", async (string? actor, string? action, string? entity,
            DateTime? from, DateTime? to, int? page, int? pageSize, IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var query = db.AuditLogs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(x => x.ActorNik == actor.Trim());
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action.Trim());
            if (!string.IsNullOrWhiteSpace(entity)) query = query.Where(x => x.Entity == entity.Trim());
            if (from is not null) query = query.Where(x => x.CreatedAt >= from.Value);
            if (to is not null) query = query.Where(x => x.CreatedAt <= to.Value);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Skip((p - 1) * size).Take(size).Select(x => new AuditRow(
                    x.Id, x.ActorNik, x.Action, x.Entity, x.EntityId, x.DetailJson, x.IpAddress, x.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(new { total, page = p, pageSize = size, items });
        }).RequireAuthorization("AdminOnly");
        return app;
    }

    public record AuditRow(Guid Id, string? ActorNik, string Action, string? Entity,
        string? EntityId, string? DetailJson, string? IpAddress, DateTime CreatedAt);
}
