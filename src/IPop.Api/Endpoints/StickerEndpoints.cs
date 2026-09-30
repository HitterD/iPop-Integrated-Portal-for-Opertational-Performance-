using IPop.Application.Chat;
using IPop.Application.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Endpoints;

/// Custom image stickers. Files are 256×256 WebP stored like chat attachments (service account on
/// the NAS, chat-files/stickers locally), and served without auth like group photos: an &lt;img&gt;
/// cannot send the bearer header and the id is an unguessable GUID.
public static class StickerEndpoints
{
    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static IEndpointRouteBuilder MapStickers(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat/stickers").RequireAuthorization();

        g.MapGet("", async (HttpContext http, IStickerService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            return userId is null ? Results.Unauthorized() : Results.Ok(await svc.ListAsync(userId.Value, ct));
        });

        // Upload a new sticker (PNG/JPG/GIF/WebP ≤ 512 KB), center-cropped to a square.
        g.MapPost("", async (HttpContext http, IStickerService svc, IFileAccessService fileSvc, IConfiguration cfg,
            IWebHostEnvironment env, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!http.Request.HasFormContentType) return Error("no_file");
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            var check = Validate(file?.FileName, file?.Length ?? 0);
            if (check is not null) return Error(check, check == "too_large" ? 413 : 422);
            await using var src = file!.OpenReadStream();
            return await StoreAsync(src, userId.Value, svc, fileSvc, cfg, env, ct);
        }).DisableAntiforgery();

        // "Jadikan stiker" from an image in a chat the caller can read.
        g.MapPost("/from-attachment/{attachmentId:guid}", async (Guid attachmentId, HttpContext http, IStickerService svc,
            IFileAccessService fileSvc, IConfiguration cfg, IWebHostEnvironment env, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var att = await db.ChatAttachments.AsNoTracking()
                .Where(a => a.Id == attachmentId && a.MessageId != null
                    && db.ChatRoomMembers.Any(m => m.RoomId == a.Message.RoomId && m.UserId == userId && m.LeftAt == null))
                .Select(a => new { a.SynologyServer, a.SynologyPath, a.FileSize, a.FileName })
                .FirstOrDefaultAsync(ct);
            if (att is null) return Error("not_found", 404);
            if (!AllowedExtensions.Contains(Path.GetExtension(att.FileName).ToLowerInvariant())) return Error("not_an_image");
            if (att.FileSize > StickerLimits.MaxSourceBytes) return Error("too_large", 413);
            try
            {
                await using var src = ChatEndpoints.TryLocalFile(env.ContentRootPath, att.SynologyServer, att.SynologyPath, out var local)
                    ? File.OpenRead(local)
                    : await ChatEndpoints.DownloadAttachmentAsync(fileSvc, att.SynologyPath, ct);
                using var buffered = new MemoryStream();
                await src.CopyToAsync(buffered, ct);
                buffered.Position = 0;
                return await StoreAsync(buffered, userId.Value, svc, fileSvc, cfg, env, ct);
            }
            catch (SynoException) { return Error("storage_unavailable", 502); }
        });

        g.MapPost("/{id:guid}/save", async (Guid id, HttpContext http, IStickerService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.SaveAsync(userId.Value, id, ct);
            return r.Success ? Results.Ok(r.Sticker) : Error(r.Error!, r.Error == "not_found" ? 404 : 422);
        });

        // Only takes it out of my collection; messages that used it keep rendering.
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IStickerService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return await svc.RemoveAsync(userId.Value, id, ct) ? Results.NoContent() : Results.NotFound();
        });

        g.MapPut("/{id:guid}/official", async (Guid id, SetOfficialRequest req, IStickerService svc, CancellationToken ct) =>
            await svc.SetOfficialAsync(id, req.Official, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("AdminOnly");

        app.MapGet("/api/chat/stickers/{id:guid}/image", async (Guid id, IStickerService svc, IFileAccessService fileSvc,
            IWebHostEnvironment env, HttpContext http, CancellationToken ct) =>
        {
            var file = await svc.GetFileAsync(id, ct);
            if (file is null) return Results.NotFound();
            // A sticker never changes: cache hard in the browser.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            if (ChatEndpoints.TryLocalFile(env.ContentRootPath, file.Server, file.Path, out var local))
                return Results.File(local, "image/webp");
            try { return Results.File(await ChatEndpoints.DownloadAttachmentAsync(fileSvc, file.Path, ct), "image/webp"); }
            catch (SynoException) { return Results.NotFound(); }
        }).AllowAnonymous();

        return app;
    }

    public record SetOfficialRequest(bool Official);

    /// Null when acceptable; otherwise no_file / not_an_image / too_large.
    public static string? Validate(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return "no_file";
        if (!AllowedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) return "not_an_image";
        if (length > StickerLimits.MaxUploadBytes) return "too_large";
        return null;
    }

    /// Center-crops to a square and scales to size×size WebP. Animated GIF/WebP keep their frames.
    public static byte[] Normalize(Stream input, int size = StickerLimits.Size)
    {
        using var image = Image.Load(input);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Crop }));
        using var ms = new MemoryStream();
        image.SaveAsWebp(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 85 });
        return ms.ToArray();
    }

    private static async Task<IResult> StoreAsync(Stream source, Guid userId, IStickerService svc, IFileAccessService fileSvc,
        IConfiguration cfg, IWebHostEnvironment env, CancellationToken ct)
    {
        byte[] webp;
        try { webp = Normalize(source); }
        catch { return Error("invalid_image"); }

        var name = $"{Guid.NewGuid():N}.webp";
        StickerFile? stored = null;
        if (fileSvc.HasServiceAccount)
        {
            var dir = $"{cfg["Synology:ChatFolder"] ?? "/home/chat"}/stickers";
            try
            {
                using var ms = new MemoryStream(webp);
                await fileSvc.UploadAsServiceAsync(dir, name, ms, ct);
                stored = new StickerFile(fileSvc.ServiceServer!, $"{dir}/{name}");
            }
            catch (SynoException) { /* fall back to local disk below */ }
        }
        if (stored is null)
        {
            var dir = Path.Combine(env.ContentRootPath, "chat-files", "stickers");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name), webp, ct);
            stored = new StickerFile("local", $"chat-files/stickers/{name}");
        }

        var r = await svc.CreateAsync(userId, stored, webp.Length, ct);
        return r.Success ? Results.Ok(r.Sticker) : Error(r.Error!);
    }

    private static IResult Error(string code, int status = 422) => Results.Json(new { code }, statusCode: status);

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
