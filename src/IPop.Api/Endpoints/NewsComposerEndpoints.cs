using System.Security.Claims;
using IPop.Application.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class NewsComposerEndpoints
{
    public static IEndpointRouteBuilder MapNewsComposer(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/news/composer").RequireAuthorization();
        g.MapPost("/drafts", async (CreateNewsDraftRequest req, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Result(await svc.CreateDraftAsync(actor.Value, req, ct));
        });
        g.MapGet("/drafts", async (HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Results.Ok(await svc.ListDraftsAsync(actor.Value, ct));
        });
        g.MapGet("/drafts/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Result(await svc.GetDraftAsync(actor.Value, id, ct));
        });
        g.MapPut("/drafts/{id:guid}", async (Guid id, SaveNewsDraftRequest req, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Result(await svc.SaveDraftAsync(actor.Value, id, req, ct));
        });
        g.MapDelete("/drafts/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Result(await svc.DiscardDraftAsync(actor.Value, id, ct));
        });
        g.MapGet("/drafts/{id:guid}/preview", async (Guid id, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var preview = await svc.PreviewAsync(actor.Value, id, ct);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });
        g.MapPost("/drafts/{id:guid}/resume", async (Guid id, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            return actor is null ? Results.Unauthorized() : Result(await svc.ResumeScheduledDraftAsync(actor.Value, id, ct));
        });
        g.MapPost("/drafts/{id:guid}/publish", async (Guid id, PromoteNewsDraftRequest req, HttpContext http, IPopDbContext db, INewsComposerService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.PromoteDraftAsync(actor.Value, id, req, ct);
            return result.Success ? Results.Ok(result) : Results.Json(result, statusCode: result.Error switch
            {
                "not_found" => 404,
                "forbidden" => 403,
                "version_conflict" or "post_content_conflict" => 409,
                _ => 422
            });
        });
        return app;
    }

    private static IResult Result(NewsDraftResultDto result) => result.Success
        ? Results.Ok(result)
        : Results.Json(result, statusCode: result.Error switch
        {
            "not_found" => 404,
            "forbidden" => 403,
            "version_conflict" or "post_content_conflict" => 409,
            _ => 422
        });

    private static Task<Guid?> ActorAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        // The access token carries the NIK in the "nik" claim and the user id in "sub"; the mapped
        // ClaimTypes.NameIdentifier is that GUID, not the NIK, so it must not be consulted first.
        // Same resolution as NewsEndpoints.GetUserIdAsync.
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(nik)) return Task.FromResult<Guid?>(null);
        return db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
