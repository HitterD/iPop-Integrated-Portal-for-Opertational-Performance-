using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachments(this IEndpointRouteBuilder app)
    {
        // Upload lives with each module (it knows the area and the owning entity);
        // download is shared and gated by the area's registered policy.
        app.MapGet("/api/attachments/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db,
            IAttachmentStore store, IEnumerable<IAttachmentAccessPolicy> policies, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (userId is null) return Results.Unauthorized();

            var area = await db.Attachments.AsNoTracking()
                .Where(a => a.Id == id && a.DeletedAt == null).Select(a => a.Area).FirstOrDefaultAsync(ct);
            if (area is null) return Results.NotFound();

            var policy = policies.FirstOrDefault(p => p.Area == area);
            if (policy is null || !await policy.CanReadAsync(id, userId.Value, ct)) return Results.Forbid();

            try
            {
                var opened = await store.OpenAsync(id, ct);
                if (opened is null) return Results.NotFound();
                var (meta, content) = opened.Value;
                return Results.File(content, meta.ContentType, meta.FileName);
            }
            catch (SynoException ex) { return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502); }
        }).RequireAuthorization();

        return app;
    }
}
