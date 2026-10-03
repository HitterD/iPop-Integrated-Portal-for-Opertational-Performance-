using System.Security.Claims;
using IPop.Infrastructure.Links;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Endpoints;

/// Link previews (WhatsApp-style cards). The server fetches; the browser never touches the target.
///   GET /api/link-preview?url=            → 200 LinkPreviewDto | 204 (nothing useful / blocked / failed)
///   GET /api/link-preview/image?u=&exp=&sig= → resized JPEG, signature instead of a Bearer header
public static class LinkPreviewEndpoints
{
    public const string RateLimitPolicy = "link-preview";
    public const string ImageRateLimitPolicy = "link-preview-image";

    public static IEndpointRouteBuilder MapLinkPreview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/link-preview", async (string? url, HttpContext http, LinkPreviewService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(ClaimTypes.Name)?.Value;
            if (string.IsNullOrEmpty(nik)) return Results.Unauthorized();
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (userId is null) return Results.Unauthorized();

            var preview = await svc.GetAsync(url, userId.Value, ct);
            // Internal cards are per-viewer; external ones are public metadata but still private to the session.
            http.Response.Headers.CacheControl = "private, max-age=300";
            return preview is null ? Results.NoContent() : Results.Ok(preview);
        }).RequireAuthorization().RequireRateLimiting(RateLimitPolicy);

        app.MapGet("/api/link-preview/image", async (string? u, long? exp, string? sig, HttpContext http,
            LinkPreviewImageSigner signer, LinkPreviewCache cache, SafeHttpFetcher fetcher, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (u is null || exp is null || !signer.Verify(u, exp.Value, sig)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return Results.NotFound();

            var key = "lp:img:" + uri.AbsoluteUri;
            if (!cache.TryGetValue(key, out byte[]? jpeg))
            {
                jpeg = await FetchAndResizeAsync(fetcher, uri, logs.CreateLogger("IPop.LinkPreview"), ct);
                cache.Set(key, jpeg ?? [], new MemoryCacheEntryOptions
                {
                    Size = Math.Max(1, (jpeg?.Length ?? 0) / 1024),
                    AbsoluteExpirationRelativeToNow = jpeg is null ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24),
                });
            }
            if (jpeg is not { Length: > 0 }) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
            return Results.File(jpeg, "image/jpeg");
        }).AllowAnonymous().RequireRateLimiting(ImageRateLimitPolicy);

        return app;
    }

    internal static bool IsAcceptedImageType(string media) =>
        media.StartsWith("image/", StringComparison.Ordinal) && !media.Contains("svg", StringComparison.Ordinal);

    internal static async Task<byte[]?> FetchAndResizeAsync(SafeHttpFetcher fetcher, Uri uri, ILogger log, CancellationToken ct)
    {
        var res = await fetcher.FetchAsync(new FetchRequest(uri, IsAcceptedImageType,
            LinkPreviewOptions.MaxImageBytes, AllowTruncate: false, Accept: "image/avif,image/webp,image/png,image/jpeg,image/gif;q=0.9"), ct);
        if (!res.Ok)
        {
            log.LogInformation("Link preview image {Host}: {Outcome}", uri.IdnHost, res.Outcome);
            return null;
        }
        return Resize(res.Body!);
    }

    /// Decodes untrusted bytes (raster formats only — ImageSharp has no SVG decoder), caps width at
    /// 600 px and re-encodes to JPEG, dropping metadata. Null if it isn't a decodable image.
    internal static byte[]? Resize(byte[] input)
    {
        try
        {
            var info = Image.Identify(input);
            // Decompression-bomb guard: refuse absurd canvases before decoding pixels.
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 40_000_000) return null;
            using var image = Image.Load(input);
            image.Mutate(x =>
            {
                x.AutoOrient();
                if (image.Width > LinkPreviewOptions.MaxImageWidth)
                    x.Resize(LinkPreviewOptions.MaxImageWidth, 0);
                x.BackgroundColor(Color.White); // transparent PNG/GIF → white, not black, in JPEG
            });
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IccProfile = null;
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 82 });
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
    }
}
