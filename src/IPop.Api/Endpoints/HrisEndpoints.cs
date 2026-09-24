using IPop.Application.Hris;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class HrisEndpoints
{
    public static IEndpointRouteBuilder MapHris(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/hris/sync", async (IHrisSyncService sync, IDirectoryChangeNotifier directory,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await sync.RunAsync(http.User.FindFirst("nik")?.Value, ct);
            if (result.Inserted > 0 || result.Updated > 0 || result.Deactivated > 0)
                await directory.NotifyAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/admin/hris/history", async (int? page, int? pageSize, IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            // The log is fetched per run; keep the list light.
            var items = await db.HrisSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt)
                .Skip((p - 1) * size).Take(size)
                .Select(r => new { r.Id, r.StartedAt, r.FinishedAt, r.Inserted, r.Updated, r.Deactivated,
                    r.Skipped, r.Succeeded, r.Error, r.TriggeredBy })
                .ToListAsync(ct);
            return Results.Ok(new { page = p, pageSize = size, items });
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/admin/hris/runs/{id:guid}/log", async (Guid id, IPopDbContext db, CancellationToken ct) =>
        {
            var run = await db.HrisSyncRuns.AsNoTracking().Where(r => r.Id == id)
                .Select(r => new { r.Id, r.Log }).FirstOrDefaultAsync(ct);
            return run is null ? Results.NotFound() : Results.Ok(new { run.Id, lines = (run.Log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries) });
        }).RequireAuthorization("AdminOnly");

        // Columns follow the admin table: Start, New, Updated, Deactivated, Status, By, Duration, Notes.
        app.MapGet("/api/admin/hris/history.csv", async (IPopDbContext db, CancellationToken ct) =>
        {
            var runs = await db.HrisSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(5000).ToListAsync(ct);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Start;New;Updated;Deactivated;Status;By;Duration;Notes");
            foreach (var r in runs)
                sb.AppendLine(string.Join(';', r.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") + "Z", r.Inserted, r.Updated, r.Deactivated,
                    r.Succeeded ? "Success" : "Failed", DirectoryEndpoints.CsvField(r.TriggeredBy ?? "Scheduled"),
                    r.FinishedAt >= r.StartedAt ? $"{(r.FinishedAt - r.StartedAt).TotalSeconds:0.0}s" : "",
                    DirectoryEndpoints.CsvField(r.Error ?? (r.Skipped > 0 ? $"{r.Skipped} protected rows skipped" : ""))));
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", "sync_history.csv");
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
