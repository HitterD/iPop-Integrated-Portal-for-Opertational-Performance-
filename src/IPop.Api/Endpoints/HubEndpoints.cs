using System.Security.Claims;
using IPop.Api.Rbac;
using IPop.Application.Attachments;
using IPop.Application.Hub;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// /api/hub — The Hub, confidential cross-department topics (docs/superpowers/specs/2026-11-21-the-hub-design.md).
/// Only members can read or write a topic; the service checks it on every call.
public static class HubEndpoints
{
    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hub").RequireAuthorization().RequirePermission(HubEvents.Permission);

        g.MapGet("/topics", (string? filter, string? q, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => Results.Ok(await svc.ListAsync(me, filter, q, ct))));

        g.MapPost("/topics", (CreateHubTopicRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.CreateAsync(me, req, ct))));

        // ?peek=1: read without marking read (e.g. a background refresh).
        g.MapGet("/topics/{id:guid}", (Guid id, string? peek, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.GetAsync(me, id, peek is not ("1" or "true"), ct))));

        g.MapPost("/topics/{id:guid}/posts", (Guid id, CreateHubPostRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.PostAsync(me, id, req, ct))));

        g.MapPut("/topics/{id:guid}/posts/{postId:guid}", (Guid id, Guid postId, EditHubPostRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.EditPostAsync(me, id, postId, req, ct))));

        g.MapDelete("/topics/{id:guid}/posts/{postId:guid}", (Guid id, Guid postId, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.DeletePostAsync(me, id, postId, ct))));

        g.MapPost("/topics/{id:guid}/posts/{postId:guid}/noted", (Guid id, Guid postId, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.NoteAsync(me, id, postId, ct))));

        // { postId } marks the best answer; null (or the current one) clears it.
        g.MapPost("/topics/{id:guid}/best", (Guid id, HubBestRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.BestAsync(me, id, req.PostId, ct))));

        g.MapPost("/topics/{id:guid}/members", (Guid id, AddHubMembersRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.AddMembersAsync(me, id, req, ct))));

        g.MapPost("/topics/{id:guid}/asks/{askId:guid}/remind", (Guid id, Guid askId, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.RemindAsync(me, id, askId, ct))));

        g.MapPost("/topics/{id:guid}/solve", (Guid id, SolveHubTopicRequest req, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.SolveAsync(me, id, req, ct))));

        g.MapPost("/topics/{id:guid}/reopen", (Guid id, HttpContext http, IHubService svc, CancellationToken ct) =>
            Run(http, async me => From(await svc.ReopenAsync(me, id, ct))));

        // A file for this topic, stored on the NAS. It is tied to a reply when the reply is posted with its id.
        g.MapPost("/topics/{id:guid}/files", async Task<IResult> (Guid id, IFormFile file, HttpContext http, IHubService svc,
            IAttachmentStore store, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            if (!await svc.IsMemberAsync(me.Value, id, ct)) return NotFound();
            try
            {
                await using var stream = file.OpenReadStream();
                var stored = await store.PutAsync(AttachmentAreas.Hub, file.FileName,
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, stream, me.Value, ct);
                await svc.LinkUploadAsync(me.Value, id, stored.Id, ct);
                return Results.Ok(new HubUploadDto(stored.Id, stored.FileName, stored.ContentType, stored.Size, $"/api/attachments/{stored.Id}"));
            }
            catch (AttachmentRejectedException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422);
            }
            catch (IPop.Application.Files.SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        }).DisableAntiforgery().RequireRateLimiting(ChatEndpoints.UploadRateLimitPolicy);

        // People to invite: active colleagues by name, with their department (no NIK or position — office rule).
        g.MapGet("/people", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var term = (q ?? "").Trim();
            if (term.Length < 2) return Results.Ok(Array.Empty<HubPersonDto>());
            var pat = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var rows = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.Id != me && EF.Functions.ILike(u.Name, pat, "\\"))
                .OrderBy(u => u.Name).Take(10)
                .Select(u => new { u.Id, u.Name, Dept = u.Department != null ? u.Department.Name : null, u.PhotoPath })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new HubPersonDto(r.Id, IPop.Application.PersonName.Display(r.Name), r.Dept,
                r.PhotoPath is { Length: > 0 } p ? $"/api/photos/{p}" : null)));
        });

        // "Usually helps with this": people I worked with most in my recent topics, plus my own department.
        g.MapGet("/people/suggested", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var myTopics = db.HubMembers.Where(m => m.UserId == me).Select(m => m.TopicId);
            var ids = await db.HubMembers.AsNoTracking().Where(m => myTopics.Contains(m.TopicId) && m.UserId != me)
                .GroupBy(m => m.UserId).OrderByDescending(x => x.Count()).Take(6).Select(x => x.Key).ToListAsync(ct);
            var rows = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive)
                .Select(u => new { u.Id, u.Name, Dept = u.Department != null ? u.Department.Name : null, u.PhotoPath }).ToListAsync(ct);
            return Results.Ok(ids.Select(id => rows.FirstOrDefault(r => r.Id == id)).Where(r => r is not null)
                .Select(r => new HubPersonDto(r!.Id, IPop.Application.PersonName.Display(r.Name), r.Dept,
                    r.PhotoPath is { Length: > 0 } p ? $"/api/photos/{p}" : null)));
        });

        // Projects I can add tasks to (for "Mark as solved" follow-ups).
        g.MapGet("/projects", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await MeAsync(http);
            if (me is null) return Results.Unauthorized();
            var rows = await (from m in db.ProjectMembers.AsNoTracking()
                              join p in db.Projects.AsNoTracking() on m.ProjectId equals p.Id
                              where m.UserId == me && p.IsActive && (m.Role == "owner" || m.Role == "manager" || m.Role == "member")
                              orderby p.Name
                              select new HubProjectOptionDto(p.Id, p.Name, p.Color)).Take(200).ToListAsync(ct);
            return Results.Ok(rows);
        });

        return app;
    }

    private static IResult From<T>(HubResult<T> r) => r.Success
        ? Results.Ok(r.Value)
        : Results.Json(new { code = r.Error, message = r.Message }, statusCode: r.Error switch
        {
            "not_found" => 404,
            "forbidden" => 403,
            "rate_limited" => 429,
            "solved" => 409,
            _ => 400,
        });

    private static IResult NotFound() => Results.Json(new { code = "not_found", message = "Topic not found." }, statusCode: 404);

    private static async Task<Guid?> MeAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
    }

    private static async Task<IResult> Run(HttpContext http, Func<Guid, Task<IResult>> body)
    {
        var me = await MeAsync(http);
        return me is null ? Results.Unauthorized() : await body(me.Value);
    }
}
