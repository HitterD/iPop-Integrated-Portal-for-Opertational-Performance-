using IPop.Application.Hris;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class HrisEndpoints
{
    public static IEndpointRouteBuilder MapHris(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/hris/sync", async (IHrisSyncService sync, IDirectoryChangeNotifier directory, CancellationToken ct) =>
        {
            var result = await sync.RunAsync(ct);
            if (result.Inserted > 0 || result.Updated > 0 || result.Deactivated > 0)
                await directory.NotifyAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/admin/hris/history", async (int? page, int? pageSize, IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var items = await db.HrisSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt)
                .Skip((p - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { page = p, pageSize = size, items });
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/admin/hris/status", async (IPopDbContext db, CancellationToken ct) =>
        {
            var state = await db.HrisSyncState.AsNoTracking().FirstOrDefaultAsync(ct);
            var count = await db.Users.CountAsync(u => u.IsActive, ct);
            return Results.Ok(new
            {
                LastSyncedAt = state?.LastIncrementalAt ?? state?.LastFullSyncAt,
                EmployeeCount = count,
            });
        }).RequireAuthorization("AdminOnly");

        return app;
    }
}
