using System.Security.Claims;
using IPop.Application.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class NewsEndpoints
{
    public static IEndpointRouteBuilder MapNews(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/news").RequireAuthorization();

        // ── Create (draft) ──────────────────────────────────────────────
        g.MapPost("", async (CreateNewsRequest req, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.CreateAsync(userId.Value, req, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: 422);
            return Results.Created($"/api/news/{result.News!.Id}", result.News);
        });

        // ── List (reader feed / moderation) ─────────────────────────────
        g.MapGet("", async (string? type, string? status, bool? pinned, bool? requiresAck,
            string? q, bool? includeScheduled, int? page, int? pageSize,
            HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var filter = new NewsListFilter(
                Type: type, Status: status, PinnedOnly: pinned, RequiresAckOnly: requiresAck,
                Search: q, Page: Math.Max(1, page ?? 1), PageSize: Math.Clamp(pageSize ?? 20, 1, 100),
                IncludeScheduled: includeScheduled ?? false);
            var result = await svc.ListAsync(userId.Value, filter, ct);
            return Results.Ok(result);
        });

        // ── Get detail ──────────────────────────────────────────────────
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var post = await svc.GetAsync(id, userId.Value, ct);
            return post is null ? Results.NotFound(new { code = "not_found", message = "News post not found." }) : Results.Ok(post);
        });

        // ── Update (draft/rejected only) ────────────────────────────────
        g.MapPatch("/{id:guid}", async (Guid id, UpdateNewsRequest req, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UpdateAsync(userId.Value, id, req, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        // ── Workflow ────────────────────────────────────────────────────
        g.MapPost("/{id:guid}/submit", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SubmitAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapPost("/{id:guid}/approve", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.ApproveAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest req, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RejectAsync(userId.Value, id, req.Reason ?? "", ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapPost("/{id:guid}/publish", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.PublishAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapPost("/{id:guid}/unpublish", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UnpublishAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        // ── Pin ─────────────────────────────────────────────────────────
        g.MapPost("/{id:guid}/pin", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.PinAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapDelete("/{id:guid}/pin", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UnpinAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        // ── Acknowledge ─────────────────────────────────────────────────
        g.MapPost("/{id:guid}/acknowledge", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.AcknowledgeAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        g.MapPost("/{id:guid}/remind-ack", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RemindAckAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var sc = result.Error == "not_found" ? 404 : result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            return Results.Ok(result.News);
        });

        // ── Audit ───────────────────────────────────────────────────────
        g.MapGet("/{id:guid}/audits", async (Guid id, HttpContext http, INewsService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var audits = await svc.GetAuditsAsync(id, userId.Value, ct);
            return Results.Ok(audits);
        });

        return app;
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value
               ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    public record RejectRequest(string? Reason);
}
