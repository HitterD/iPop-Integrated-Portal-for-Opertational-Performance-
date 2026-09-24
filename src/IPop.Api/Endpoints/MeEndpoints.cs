using IPop.Application.Realtime;
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

        return app;
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
