using System.Security.Claims;
using IPop.Application.News;
using IPop.Core.Entities;
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

        g.MapGet("/{id:guid}/discussion", async (Guid id, HttpContext http, INewsService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (await svc.GetAsync(id, userId.Value, ct) is null) return Results.NotFound();
            return Results.Ok(await GetDiscussionAsync(db, id, userId.Value, ct));
        });

        g.MapPost("/{id:guid}/like", async (Guid id, HttpContext http, INewsService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (await svc.GetAsync(id, userId.Value, ct) is not { Status: "published" }) return Results.NotFound();
            if (!await db.NewsLikes.AnyAsync(l => l.NewsPostId == id && l.UserId == userId.Value, ct))
            {
                db.NewsLikes.Add(new NewsLike { NewsPostId = id, UserId = userId.Value, CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(await GetDiscussionAsync(db, id, userId.Value, ct));
        });

        g.MapDelete("/{id:guid}/like", async (Guid id, HttpContext http, INewsService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (await svc.GetAsync(id, userId.Value, ct) is null) return Results.NotFound();
            await db.NewsLikes.Where(l => l.NewsPostId == id && l.UserId == userId.Value).ExecuteDeleteAsync(ct);
            return Results.Ok(await GetDiscussionAsync(db, id, userId.Value, ct));
        });

        g.MapPost("/{id:guid}/comments", async (Guid id, CommentRequest req, HttpContext http,
            INewsService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            if (await svc.GetAsync(id, userId.Value, ct) is not { Status: "published" }) return Results.NotFound();
            var body = req.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > 2000)
                return Results.Json(new { code = "invalid_comment", message = "Comment must contain 1–2000 characters." }, statusCode: 422);
            db.NewsComments.Add(new NewsComment { Id = Guid.NewGuid(), NewsPostId = id, AuthorId = userId.Value, Body = body, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await GetDiscussionAsync(db, id, userId.Value, ct));
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var post = await db.NewsPosts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (post is null) return Results.NotFound();
            var isAdmin = await db.UserRoles.AnyAsync(ur => ur.UserId == userId.Value && ur.Role.Name == "Admin", ct);
            if (post.AuthorId != userId.Value && !isAdmin) return Results.Forbid();
            db.NewsPosts.Remove(post);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        });

        return app;
    }

    public static async Task<DiscussionDto> GetDiscussionAsync(IPopDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var comments = await db.NewsComments.AsNoTracking().Where(c => c.NewsPostId == id)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CommentDto(c.Id, c.Author.Name, c.Body, c.CreatedAt))
            .Take(100).ToListAsync(ct);
        var count = await db.NewsLikes.CountAsync(l => l.NewsPostId == id, ct);
        var liked = await db.NewsLikes.AnyAsync(l => l.NewsPostId == id && l.UserId == userId, ct);
        return new DiscussionDto(count, liked, comments);
    }

    public record CommentRequest(string? Body);
    public record CommentDto(Guid Id, string AuthorName, string Body, DateTime CreatedAt);
    public record DiscussionDto(int LikeCount, bool IsLiked, IReadOnlyList<CommentDto> Comments);

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
