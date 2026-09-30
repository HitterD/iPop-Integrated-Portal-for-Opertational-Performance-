using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        // Seeds the sidebar badges; live updates arrive as badgeChanged(key, count).
        app.MapGet("/api/me/badges", async (HttpContext http, IPopDbContext db,
            IEnumerable<IBadgeProvider> providers, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik)
                .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await BadgesCore(providers, userId.Value, ct));
        }).RequireAuthorization();

        // Tutorial progress per module: { "chat": "done", "mail": "dismissed" }. Missing = never seen.
        app.MapGet("/api/me/tutorials", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var rows = await db.UserTutorialStates.AsNoTracking().Where(t => t.UserId == userId)
                .Select(t => new { t.ModuleKey, t.Status }).ToListAsync(ct);
            return Results.Ok(rows.ToDictionary(r => r.ModuleKey, r => r.Status));
        }).RequireAuthorization();

        app.MapPut("/api/me/tutorials/{module}", async (string module, TutorialStatusRequest req,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (module.Length is 0 or > 32 || req.Status is not ("done" or "dismissed"))
                return Results.BadRequest();
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var row = await db.UserTutorialStates.FirstOrDefaultAsync(t => t.UserId == userId && t.ModuleKey == module, ct);
            if (row is null)
            {
                row = new UserTutorialState { UserId = userId.Value, ModuleKey = module, Status = req.Status };
                db.UserTutorialStates.Add(row);
            }
            // "dismissed" never downgrades a finished tour.
            else if (req.Status == "done") row.Status = "done";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapDelete("/api/me/tutorials", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            await db.UserTutorialStates.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    public record TutorialStatusRequest(string Status);

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    /// Sequential on purpose: providers share the request's DbContext, which is not thread-safe.
    internal static async Task<Dictionary<string, int>> BadgesCore(
        IEnumerable<IBadgeProvider> providers, Guid userId, CancellationToken ct)
    {
        var result = new Dictionary<string, int>();
        foreach (var p in providers) result[p.Key] = await p.CountAsync(userId, ct);
        return result;
    }
}
