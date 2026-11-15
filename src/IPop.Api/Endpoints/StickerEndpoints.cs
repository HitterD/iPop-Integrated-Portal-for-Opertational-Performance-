using IPop.Application.Chat;
using IPop.Application.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Endpoints;

/// Custom image stickers. Files are 512×512 WebP (older ones 256×256) stored like chat attachments (service account on
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

        // Upload a new sticker (PNG/JPG/GIF/WebP ≤ 10 MB), fitted into a transparent square.
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

        // "Make sticker" from an image in a chat the caller can read.
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

        // Star / un-star any sticker (custom, Fluent default or legacy built-in) by its message key.
        g.MapPut("/favorites", async (FavoriteRequest req, HttpContext http, IStickerService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return await svc.SetFavoriteAsync(userId.Value, req.Key ?? "", req.Favorite, ct) ? Results.NoContent() : Error("invalid_key");
        });

        g.MapPut("/{id:guid}/official", async (Guid id, SetOfficialRequest req, IStickerService svc, CancellationToken ct) =>
            await svc.SetOfficialAsync(id, req.Official, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("AdminOnly");

        app.MapGet("/api/chat/stickers/{id:guid}/image", async (Guid id, IStickerService svc, IFileAccessService fileSvc,
            IWebHostEnvironment env, HttpContext http, CancellationToken ct) =>
        {
            var file = await svc.GetFileAsync(id, ct);
            if (file is null) return Results.NotFound();
            // A sticker never changes: cache hard in the browser — but only once the bytes are in
            // hand, so a passing NAS error is not remembered as a year-long 404.
            const string forever = "public, max-age=31536000, immutable";
            if (ChatEndpoints.TryLocalFile(env.ContentRootPath, file.Server, file.Path, out var local))
            {
                http.Response.Headers.CacheControl = forever;
                return Results.File(local, "image/webp");
            }
            try
            {
                var stream = await ChatEndpoints.DownloadAttachmentAsync(fileSvc, file.Path, ct);
                http.Response.Headers.CacheControl = forever;
                return Results.File(stream, "image/webp");
            }
            catch (SynoException)
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        return app;
    }

    public record SetOfficialRequest(bool Official);
    public record FavoriteRequest(string? Key, bool Favorite);

    /// Null when acceptable; otherwise no_file / not_an_image / too_large.
    public static string? Validate(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return "no_file";
        if (!AllowedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) return "not_an_image";
        if (length > StickerLimits.MaxUploadBytes) return "too_large";
        return null;
    }

    /// Fits the image inside a size×size square (contain, transparent padding: never crops a face
    /// off) and encodes WebP. A client-made sticker is already exactly size×size and is only
    /// re-encoded. Animated GIF/WebP keep their frames. Quality steps down until the file meets
    /// the static/animated target; animations then drop every other frame (keeping total timing).
    public static byte[] Normalize(Stream input, int size = StickerLimits.Size)
    {
        using var image = Image.Load<Rgba32>(input);
        var animated = image.Frames.Count > 1;
        if (animated) CarryOverFrameTiming(image);
        image.Mutate(x =>
        {
            x.AutoOrient();
            if (image.Width != size || image.Height != size)
                x.Resize(new ResizeOptions
                {
                    Size = new Size(size, size), Mode = ResizeMode.Pad, PadColor = Color.Transparent,
                    Sampler = KnownResamplers.Lanczos3,
                });
        });

        var target = animated ? StickerLimits.TargetAnimatedBytes : StickerLimits.TargetStaticBytes;
        int[] qualities = animated ? [80, 65, 50] : [88, 80, 70, 60, 50, 40];
        while (true)
        {
            byte[] best = [];
            foreach (var q in qualities)
            {
                best = Encode(image, q);
                if (best.Length <= target) return best;
            }
            // Still too big: an animation can lose frames, a still image has nothing left to give.
            if (!animated || image.Frames.Count < 4) return best;
            DropEveryOtherFrame(image);
        }
    }

    private static byte[] Encode(Image image, int quality)
    {
        using var ms = new MemoryStream();
        image.SaveAsWebp(ms, new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossy, Quality = quality, TransparentColorMode = WebpTransparentColorMode.Preserve,
        });
        return ms.ToArray();
    }

    /// GIF frame delays (centiseconds) → WebP frame delays (ms), so a converted GIF keeps its speed.
    private static void CarryOverFrameTiming(Image image)
    {
        if (image.Metadata.TryGetGifMetadata(out var gif))
            image.Metadata.GetWebpMetadata().RepeatCount = gif.RepeatCount;
        foreach (var frame in image.Frames)
        {
            if (frame.Metadata.TryGetGifMetadata(out var g))
                frame.Metadata.GetWebpMetadata().FrameDelay = (uint)Math.Max(2, g.FrameDelay) * 10;
        }
    }

    private static void DropEveryOtherFrame(Image image)
    {
        var last = image.Frames.Count - 1;
        for (var i = last % 2 == 1 ? last : last - 1; i >= 1; i -= 2)
        {
            var dropped = image.Frames[i].Metadata.GetWebpMetadata().FrameDelay;
            image.Frames[i - 1].Metadata.GetWebpMetadata().FrameDelay += dropped;
            image.Frames.RemoveFrame(i);
        }
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
        // The access token already carries the user id (sub); only tokens without it fall back to the NIK lookup.
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
