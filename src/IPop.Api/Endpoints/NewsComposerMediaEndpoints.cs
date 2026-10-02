using System.Security.Claims;
using System.Security.Cryptography;
using IPop.Application.News;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// <summary>
/// Composer media for the full-page News editor. Upload is bearer-authorized; delivery is
/// ticket-authorized so <c>&lt;img&gt;</c>/<c>&lt;video&gt;</c> elements (which cannot send the bearer
/// header) can fetch it and the player can seek.
///
/// <para>The ticket names (media, user, context) and is short-lived. Every request re-runs the
/// access check against the *current* reference graph — audience, draft ownership, user active — so a
/// revoked draft, a narrowed audience or a deactivated account closes access immediately. Tickets are
/// minted per response and never persisted. Restricted content is never cached (no-store).</para>
/// </summary>
public static class NewsComposerMediaEndpoints
{
    private const string TicketPurpose = "IPop.NewsComposer.Media";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(30);

    /// Endpoint-scoped request cap: the video limit plus a megabyte of multipart overhead. The app-wide
    /// Kestrel limit is left alone.
    private const long MaxUploadRequestBytes = NewsComposerMediaLimits.MaxVideoBytes + 1024 * 1024;

    public static IEndpointRouteBuilder MapNewsComposerMedia(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/news/composer").RequireAuthorization();

        // ── Upload ──────────────────────────────────────────────────────
        // POST /api/news/composer/drafts/{draftId}/media?kind=image|video|file
        g.MapPost("/drafts/{draftId:guid}/media", async (Guid draftId, string? kind, HttpContext http,
            IPopDbContext db, INewsComposerMediaService svc, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();

            // Authorize BEFORE reading the form: the body can be 100 MB, so an unauthorized caller
            // must not be able to make the server spool it. UploadAsync re-checks as the authority.
            var gate = await svc.AuthorizeUploadAsync(actor.Value, draftId, ct);
            if (gate is not null) return Error(gate, StatusFor(gate));

            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = MaxUploadRequestBytes }));
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Error("file_required", 422);

            var temp = await SaveTempAsync(file, ct);
            try
            {
                _ = int.TryParse(form["durationSeconds"], out var duration);
                var result = await svc.UploadAsync(actor.Value, new NewsComposerMediaUpload(
                    draftId, kind ?? "image", temp, file.FileName, file.ContentType, Math.Max(0, duration)), ct);
                if (!result.Success) return Error(result.Error!, StatusFor(result.Error!));

                var media = result.Media!;
                // Videos archive to the NAS after the response: the editor is not kept waiting on a
                // 100 MB transfer, and a failure just leaves the local copy serving it.
                if (media.Kind == "video")
                {
                    var id = media.MediaId;
                    // Resolve the logger now: RequestServices is disposed once the response completes.
                    var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NewsComposerMedia");
                    _ = Task.Run(async () =>
                    {
                        // Never let a background failure become an unobserved task exception.
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            var bg = scope.ServiceProvider.GetRequiredService<INewsComposerMediaService>();
                            await bg.ArchiveVideoAsync(id, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "Background NAS archive of composer video {Id} failed; the local copy still serves it.", id);
                        }
                    });
                }
                NoStore(http);  // the payload carries a ticket — never cache it
                return Results.Ok(await MintAsync(media, http, actor.Value));
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadRequestBytes))
        .DisableAntiforgery();

        // ── Manifest: every asset the editor/reader may render, with fresh tickets ──
        g.MapGet("/drafts/{draftId:guid}/media", async (Guid draftId, HttpContext http, IPopDbContext db,
            INewsComposerMediaService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var list = await svc.ListDraftMediaAsync(actor.Value, draftId, ct);
            if (list is null) return Results.NotFound();
            var minted = new List<NewsComposerMediaDto>(list.Count);
            foreach (var m in list) minted.Add(await MintAsync(m, http, actor.Value));
            NoStore(http);  // the payload carries tickets — never cache it
            return Results.Ok(minted);
        });

        g.MapGet("/posts/{postId:guid}/media", async (Guid postId, HttpContext http, IPopDbContext db,
            INewsComposerMediaService svc, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor is null) return Results.Unauthorized();
            var list = await svc.ListPostMediaAsync(actor.Value, postId, ct);
            if (list is null) return Results.NotFound();
            var minted = new List<NewsComposerMediaDto>(list.Count);
            foreach (var m in list) minted.Add(await MintAsync(m, http, actor.Value));
            NoStore(http);  // the payload carries tickets — never cache it
            return Results.Ok(minted);
        });

        // ── Delivery (ticket-authenticated) ─────────────────────────────
        var media = app.MapGroup("/api/news/composer/media").AllowAnonymous();

        media.MapGet("/{id:guid}", async (Guid id, string? t, bool? poster, HttpContext http,
            INewsComposerMediaService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (ReadTicket(dp, t, id) is not { } ticket) return Results.Unauthorized();

            var f = await svc.OpenAsync(id, ticket.UserId, ticket.ContextKind, ticket.ContextId, poster == true, ct);
            if (f is null) return Results.NotFound();
            // Restricted, private content: never cached, whatever the kind. No filename → inline
            // disposition, so a video/image URL plays in place instead of downloading.
            NoStore(http);

            // A local video streams straight off disk with Range so the player can seek.
            if (f.FullPath is not null) return Results.File(f.FullPath, f.ContentType, enableRangeProcessing: true);
            return Results.File(f.Content!, f.ContentType);
        });

        return app;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Tickets
    // ──────────────────────────────────────────────────────────────────────

    private static Task<NewsComposerMediaDto> MintAsync(NewsComposerMediaDto media, HttpContext http, Guid actorId)
    {
        // Infrastructure never sees tickets; the API layer mints them per response.
        var token = Uri.EscapeDataString(Protector(http).Protect(
            $"{media.MediaId:N}|{actorId:N}|{media.ContextKind}|{media.ContextId:N}", TicketLifetime));
        var basePath = $"/api/news/composer/media/{media.MediaId}";
        return Task.FromResult(media with
        {
            StreamUrl = $"{basePath}?t={token}",
            PosterUrl = media.HasPoster ? $"{basePath}?t={token}&poster=true" : null,
        });
    }

    internal static (Guid UserId, string ContextKind, Guid ContextId)? ReadTicket(IDataProtectionProvider dp, string? ticket, Guid mediaId)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        try
        {
            var parts = Protector(dp).Unprotect(ticket).Split('|');
            if (parts.Length != 4) return null;
            if (!Guid.TryParse(parts[0], out var mid) || mid != mediaId) return null;
            if (!Guid.TryParse(parts[1], out var uid) || !Guid.TryParse(parts[3], out var ctx)) return null;
            if (parts[2] is not ("draft" or "post")) return null;
            return (uid, parts[2], ctx);
        }
        catch (CryptographicException) { return null; }
    }

    /// Mint a ticket for (media, user, context). Exposed so the ticket lifetime/shape can be verified.
    internal static string MintTicket(IDataProtectionProvider dp, Guid mediaId, Guid userId, string contextKind, Guid contextId) =>
        Protector(dp).Protect($"{mediaId:N}|{userId:N}|{contextKind}|{contextId:N}", TicketLifetime);

    private static ITimeLimitedDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(TicketPurpose).ToTimeLimitedDataProtector();

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) =>
        dp.CreateProtector(TicketPurpose).ToTimeLimitedDataProtector();

    /// Private, restricted content: never let a proxy or the browser reuse it.
    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "private, no-store";

    private static async Task<Guid?> ActorIdAsync(HttpContext http, IPopDbContext db)
    {
        // The access token carries the NIK in the "nik" claim and the user id in "sub"; the mapped
        // ClaimTypes.NameIdentifier is that GUID, not the NIK, so it must not be consulted first.
        // Same resolution as NewsEndpoints.GetUserIdAsync.
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
    }

    private static Task<Guid?> ActorAsync(HttpContext http, IPopDbContext db, CancellationToken ct) => ActorIdAsync(http, db);

    private static async Task<string> SaveTempAsync(IFormFile file, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-news-media-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using var dst = File.Create(path);
        await file.CopyToAsync(dst, ct);
        return path;
    }

    private static int StatusFor(string code) => code switch
    {
        "forbidden" => 403,
        "not_found" => 404,
        "too_large" => 413,
        "video_limit" or "storage_unavailable" => 409,
        _ => 422,
    };

    private static IResult Error(string code, int status) => Results.Json(new { code, message = code }, statusCode: status);
}