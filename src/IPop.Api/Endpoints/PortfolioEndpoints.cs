using IPop.Api.Rbac;
using IPop.Application.Projects;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapProjectPortfolio(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/portfolio").RequireAuthorization().RequirePermission("projects.read");
        group.MapGet("", async (int? page, int? pageSize, DateTime? localToday, HttpContext http, IPortfolioService service, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            return userId.HasValue ? Results.Ok(await service.GetPortfolioAsync(userId.Value, ct, today: localToday, page: Math.Max(1, page ?? 1), pageSize: Math.Clamp(pageSize ?? 20, 1, 100))) : Results.Unauthorized();
        });
        group.MapGet("/tasks", async (string? filter, Guid? projectId, Guid? assigneeId,
            int? page, int? pageSize, DateTime? localToday, HttpContext http, IPortfolioService service, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (!userId.HasValue) return Results.Unauthorized();
            if (filter is not ("overdue" or "due_week" or "workload"))
                return Results.BadRequest(new { code = "invalid_filter" });
            return Results.Ok(await service.GetTasksAsync(userId.Value, filter, projectId,
                assigneeId, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 100), ct, localToday));
        });
        return app;
    }

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrWhiteSpace(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive)
            .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
