using System.Security.Claims;
using System.Text.Json;
using IPop.Application.News;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Launch-video announcements. &lt;video src&gt; cannot send the bearer header and a 15-minute JWT would
/// expire mid-playback, so the stream/poster URLs carry a signed ticket bound to (video, user).
public static class NewsVideoEndpoints
{
    private const long MaxUploadRequestBytes = NewsVideoLimits.MaxBytes + NewsVideoLimits.MaxPosterBytes + 1024 * 1024;
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(6);
    private const string TicketPurpose = "IPop.NewsVideo.Stream";

    /// What the client needs to render a video: metadata plus ready-to-use URLs.
    public record NewsVideoView(NewsVideoDto Video, string StreamUrl, string? PosterUrl, string DownloadUrl);

    public record SetPopupRequest(DateTime? PopupUntil);

    public static IEndpointRouteBuilder MapNewsVideos(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/news-videos").RequireAuthorization("AdminOnly");

        admin.MapGet("", async (HttpContext http, INewsVideoService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.ListAsync(userId.Value, ct));
        });

        admin.MapPost("", async (HttpContext http, INewsVideoService svc, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = MaxUploadRequestBytes }));
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Error("file_required", 422);
            if (file.Length > NewsVideoLimits.MaxBytes) return Error("too_large", 413);
            if (!NewsVideoService.IsAllowedVideo(file.FileName)) return Error("blocked_type", 422);
            var poster = form.Files.GetFile("poster");
            if (poster is { Length: > NewsVideoLimits.MaxPosterBytes }) return Error("invalid_poster", 422);

            IReadOnlyList<NewsTargetInput>? targets = null;
            if (form["targets"].ToString() is { Length: > 0 } tj)
            {
                try { targets = JsonSerializer.Deserialize<List<NewsTargetInput>>(tj, JsonSerializerOptions.Web); }
                catch (JsonException) { return Error("invalid_targets", 422); }
            }
            DateTime? until = null;
            if (int.TryParse(form["popupDays"], out var days))
            {
                if (days is < 1 or > 365) return Error("invalid_popup_days", 422);
                until = DateTime.UtcNow.AddDays(days);
            }
            _ = int.TryParse(form["durationSeconds"], out var duration);

            var videoTmp = await SaveTempAsync(file, ct);
            var posterTmp = poster is { Length: > 0 } ? await SaveTempAsync(poster, ct) : null;
            try
            {
                var result = await svc.UploadAsync(userId.Value, new NewsVideoUpload(
                    form["title"].ToString(), form["description"].ToString(), videoTmp, file.FileName,
                    file.ContentType, duration, posterTmp, until, targets), ct);
                if (!result.Success) return Error(result.Error!, result.Error == "forbidden" ? 403 : 422);

                // NAS archive in the background: the admin gets the response as soon as the local copy is live.
                var id = result.Video!.Id;
                _ = Task.Run(async () =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var bg = (NewsVideoService)scope.ServiceProvider.GetRequiredService<INewsVideoService>();
                    await bg.ArchiveAsync(id, CancellationToken.None);
                });
                return Results.Created($"/api/admin/news-videos/{id}", result.Video);
            }
            finally
            {
                // The service moved what it kept; anything left is a rejected upload.
                if (File.Exists(videoTmp)) File.Delete(videoTmp);
                if (posterTmp is not null && File.Exists(posterTmp)) File.Delete(posterTmp);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadRequestBytes))
        .DisableAntiforgery();

        admin.MapPatch("/{id:guid}/popup", async (Guid id, SetPopupRequest req, HttpContext http, INewsVideoService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SetPopupAsync(userId.Value, id, req.PopupUntil, ct);
            if (!result.Success) return Error(result.Error!, StatusFor(result.Error!));
            return Results.Ok(result.Video);
        });

        admin.MapDelete("/{id:guid}", async (Guid id, HttpContext http, INewsVideoService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.DeleteAsync(userId.Value, id, ct);
            if (!result.Success) return Error(result.Error!, StatusFor(result.Error!));
            return Results.NoContent();
        });

        var g = app.MapGroup("/api/news/videos").RequireAuthorization();

        g.MapGet("/popup", async (HttpContext http, INewsVideoService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var v = await svc.GetPendingPopupAsync(userId.Value, ct);
            return v is null ? Results.NoContent() : Results.Ok(View(v, userId.Value, dp));
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, INewsVideoService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            var v = await svc.GetAsync(id, userId.Value, ct);
            return v is null ? Results.NotFound(new { code = "not_found", message = "Video not found." }) : Results.Ok(View(v, userId.Value, dp));
        });

        g.MapPost("/{id:guid}/track/{kind}", async (Guid id, string kind, HttpContext http, INewsVideoService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http);
            if (userId is null) return Results.Unauthorized();
            return await svc.TrackAsync(userId.Value, id, kind, ct) ? Results.NoContent() : Results.NotFound();
        });

        // Ticket-authenticated: the browser's media element fetches these directly (with Range).
        var media = app.MapGroup("/api/news/videos").AllowAnonymous();

        media.MapGet("/{id:guid}/stream", async (Guid id, string? t, bool? download, INewsVideoService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (ReadTicket(dp, t, id) is not { } userId) return Results.Unauthorized();
            var f = await svc.OpenFileAsync(id, poster: false, ct);
            if (f is null) return Results.NotFound();
            if (download == true)
            {
                await svc.TrackAsync(userId, id, "downloaded", ct);
                return Results.File(f.FullPath, f.ContentType, f.FileName, enableRangeProcessing: true);
            }
            return Results.File(f.FullPath, f.ContentType, enableRangeProcessing: true);
        });

        media.MapGet("/{id:guid}/poster", async (Guid id, string? t, INewsVideoService svc, IDataProtectionProvider dp, HttpContext http, CancellationToken ct) =>
        {
            if (ReadTicket(dp, t, id) is null) return Results.Unauthorized();
            var f = await svc.OpenFileAsync(id, poster: true, ct);
            if (f is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(f.FullPath, f.ContentType);
        });

        return app;
    }

    private static NewsVideoView View(NewsVideoDto v, Guid userId, IDataProtectionProvider dp)
    {
        var t = Uri.EscapeDataString(Protector(dp).Protect($"{v.Id:N}|{userId:N}", TicketLifetime));
        var basePath = $"/api/news/videos/{v.Id}";
        return new NewsVideoView(v, $"{basePath}/stream?t={t}", v.HasPoster ? $"{basePath}/poster?t={t}" : null,
            $"{basePath}/stream?t={t}&download=true");
    }

    private static Guid? ReadTicket(IDataProtectionProvider dp, string? ticket, Guid videoId)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        try
        {
            var parts = Protector(dp).Unprotect(ticket).Split('|');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var vid) || vid != videoId) return null;
            return Guid.TryParse(parts[1], out var uid) ? uid : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) =>
        dp.CreateProtector(TicketPurpose).ToTimeLimitedDataProtector();

    private static async Task<string> SaveTempAsync(IFormFile file, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-video-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using var dst = File.Create(path);
        await file.CopyToAsync(dst, ct);
        return path;
    }

    private static int StatusFor(string code) => code switch { "forbidden" => 403, "not_found" => 404, _ => 422 };

    private static IResult Error(string code, int status) => Results.Json(new { code, message = code }, statusCode: status);

    private static async Task<Guid?> GetUserIdAsync(HttpContext http)
    {
        var nik = http.User.FindFirst("nik")?.Value
               ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var db = http.RequestServices.GetRequiredService<IPopDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }
}
