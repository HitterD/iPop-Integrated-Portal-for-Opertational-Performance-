using System.ComponentModel.DataAnnotations;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Self-service profile: the caller's email, IP-phone extension and photo. Name, position and placement stay
/// admin-owned, so there is deliberately no endpoint for them here.
public static class ProfileEndpoints
{
    internal const int MaxPhotoBytes = 512 * 1024;
    internal const int MaxPhotoSide = 512;
    internal const int MaxLargeBytes = 2 * 1024 * 1024;
    internal const int MaxLargeSide = 1024;

    public static IEndpointRouteBuilder MapProfile(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/me/email", async Task<IResult> (EmailRequest req, HttpContext http, IPopDbContext db,
            IDirectoryChangeNotifier notifier, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            var (status, error) = await ChangeEmailAsync(db, nik, req.Email, ct);
            if (status != 200) return Results.Json(error, statusCode: status);
            await notifier.NotifyAsync(ct);
            return Results.Ok(new { email = req.Email.Trim() });
        }).RequireAuthorization();

        // IP-phone extension (Directory "Ext."). Empty clears it.
        app.MapPut("/api/me/extension", async Task<IResult> (ExtensionRequest req, HttpContext http, IPopDbContext db,
            IDirectoryChangeNotifier notifier, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            var (status, error, value) = await ChangeExtensionAsync(db, nik, nik, req.Extension, "profile.extension.change", ct);
            if (status != 200) return Results.Json(error, statusCode: status);
            await notifier.NotifyAsync(ct);
            return Results.Ok(new { extension = value });
        }).RequireAuthorization();

        // "file" is the 256px avatar; the optional "large" part is the <=1024px copy for the viewer.
        app.MapPost("/api/me/photo", async Task<IResult> (HttpContext http, IPopDbContext db,
            IDirectoryChangeNotifier notifier, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (!http.Request.HasFormContentType) return Results.Json(new ProfileError("photo_size", "No photo was sent."), statusCode: 400);
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.Json(new ProfileError("photo_size", "No photo was sent."), statusCode: 400);
            var large = form.Files.GetFile("large");
            var (status, error, id) = await SetPhotoAsync(db, nik, await ReadAllAsync(file, ct),
                large is null ? null : await ReadAllAsync(large, ct), ct);
            if (status != 200) return Results.Json(error, statusCode: status);
            await notifier.NotifyAsync(ct);
            return Results.Ok(new { photoId = id });
        }).RequireAuthorization().DisableAntiforgery()
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxPhotoBytes + MaxLargeBytes + 64 * 1024));

        app.MapDelete("/api/me/photo", async Task<IResult> (HttpContext http, IPopDbContext db,
            IDirectoryChangeNotifier notifier, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (!await RemovePhotoAsync(db, nik, ct)) return Results.Unauthorized();
            await notifier.NotifyAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Which people have a photo, as nik → photo id. The client caches it and renders
        // avatars from it, so no list DTO has to carry photo fields.
        app.MapGet("/api/photos", async (IPopDbContext db, CancellationToken ct) =>
            await db.Users.AsNoTracking().Where(u => u.PhotoPath != null && u.IsActive)
                .ToDictionaryAsync(u => u.Nik, u => u.PhotoPath!, ct))
            .RequireAuthorization();

        // Anonymous on purpose: <img> cannot send a bearer token. The id is a random GUID that
        // changes on every upload, so it is unguessable and the bytes behind it never change.
        app.MapGet("/api/photos/{id:guid}", async Task<IResult> (Guid id, IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var p = await db.UserPhotos.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Content, x.ContentType }).FirstOrDefaultAsync(ct);
            if (p is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(p.Content, p.ContentType);
        }).AllowAnonymous();

        // Full size for the photo viewer; photos uploaded before the large copy existed fall back to 256px.
        app.MapGet("/api/photos/{id:guid}/large", async Task<IResult> (Guid id, IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var p = await db.UserPhotos.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { Content = x.LargeContent ?? x.Content, ContentType = x.LargeContentType ?? x.ContentType })
                .FirstOrDefaultAsync(ct);
            if (p is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(p.Content, p.ContentType);
        }).AllowAnonymous();

        return app;
    }

    public record EmailRequest(string Email);
    public record ExtensionRequest(string? Extension);
    public record ProfileError(string Code, string Message);

    internal static async Task<(int Status, ProfileError? Error)> ChangeEmailAsync(
        IPopDbContext db, string? nik, string? email, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
        if (user is null) return (401, null);

        var value = email?.Trim() ?? "";
        if (value.Length is 0 or > 320 || !new EmailAddressAttribute().IsValid(value) || !value.Contains('.'))
            return (400, new("invalid_email", "Enter a valid email address."));
        if (string.Equals(user.Email, value, StringComparison.Ordinal)) return (200, null);

        var lower = value.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Id != user.Id && u.Email.ToLower() == lower, ct))
            return (409, new("email_taken", "Another account already uses this email."));

        var now = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik, Action = "profile.email.change", Entity = "User", EntityId = user.Nik,
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { from = user.Email, to = value }),
            CreatedAt = now,
        });
        user.Email = value;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (200, null);
    }

    /// Extensions are 2–8 digits; several people may share one (a desk phone). Null/blank clears it.
    internal static bool TryNormalizeExtension(string? raw, out string? value)
    {
        value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return value is null || (value.Length is >= 2 and <= 8 && value.All(char.IsAsciiDigit));
    }

    internal static async Task<(int Status, ProfileError? Error, string? Value)> ChangeExtensionAsync(
        IPopDbContext db, string? actorNik, string? targetNik, string? extension, string action, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == targetNik, ct);
        if (user is null) return (actorNik == targetNik ? 401 : 404, null, null);
        if (!TryNormalizeExtension(extension, out var value))
            return (400, new("invalid_extension", "Use 2–8 digits, e.g. 1133."), null);
        if (string.Equals(user.Extension, value, StringComparison.Ordinal)) return (200, null, value);

        var now = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = actorNik, Action = action, Entity = "User", EntityId = user.Nik,
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { from = user.Extension, to = value }),
            CreatedAt = now,
        });
        user.Extension = value;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (200, null, value);
    }

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    internal static async Task<(int Status, ProfileError? Error, Guid? Id)> SetPhotoAsync(
        IPopDbContext db, string? nik, byte[] bytes, byte[]? large, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
        if (user is null) return (401, null, null);
        if (bytes.Length == 0 || bytes.Length > MaxPhotoBytes)
            return (400, new("photo_size", "The photo must be under 512 KB after cropping."), null);

        var type = ImageSniff.Detect(bytes);
        if (type is null) return (400, new("photo_type", "Use a JPG, PNG or WebP image."), null);
        var size = ImageSniff.Dimensions(bytes, type);
        if (size is not { } d || d.W < 32 || d.H < 32 || d.W > MaxPhotoSide || d.H > MaxPhotoSide)
            return (400, new("photo_dimensions", $"The photo must be between 32 and {MaxPhotoSide} pixels on each side."), null);

        string? largeType = null;
        if (large is { Length: > 0 })
        {
            if (large.Length > MaxLargeBytes)
                return (400, new("photo_size", "The full-size photo must be under 2 MB."), null);
            largeType = ImageSniff.Detect(large);
            if (largeType is null) return (400, new("photo_type", "Use a JPG, PNG or WebP image."), null);
            if (ImageSniff.Dimensions(large, largeType) is not { } ld || ld.W < 32 || ld.H < 32 || ld.W > MaxLargeSide || ld.H > MaxLargeSide)
                return (400, new("photo_dimensions", $"The full-size photo must be at most {MaxLargeSide} pixels on each side."), null);
        }
        else large = null;

        var now = DateTime.UtcNow;
        await db.UserPhotos.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(ct);
        var photo = new UserPhoto
        {
            Id = Guid.NewGuid(), UserId = user.Id, Content = bytes, ContentType = type,
            LargeContent = large, LargeContentType = largeType, CreatedAt = now,
        };
        db.UserPhotos.Add(photo);
        user.PhotoPath = photo.Id.ToString();
        user.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik, Action = "profile.photo.change", Entity = "User", EntityId = user.Nik,
            DetailJson = "{}", CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return (200, null, photo.Id);
    }

    internal static async Task<bool> RemovePhotoAsync(IPopDbContext db, string? nik, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
        if (user is null) return false;
        if (user.PhotoPath is null) return true;
        await db.UserPhotos.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(ct);
        user.PhotoPath = null;
        user.UpdatedAt = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik, Action = "profile.photo.remove", Entity = "User", EntityId = user.Nik,
            DetailJson = "{}", CreatedAt = user.UpdatedAt,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// Header-only image checks, so the server can reject non-images and oversized frames
/// without an imaging library. The browser already cropped and re-encoded the file.
internal static class ImageSniff
{
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length > 12 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 30 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public static (int W, int H)? Dimensions(ReadOnlySpan<byte> b, string type) => type switch
    {
        "image/png" when b.Length >= 24 => (BE32(b[16..]), BE32(b[20..])),
        "image/webp" => WebP(b),
        "image/jpeg" => Jpeg(b),
        _ => null,
    };

    private static int BE32(ReadOnlySpan<byte> b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    private static int BE16(ReadOnlySpan<byte> b) => (b[0] << 8) | b[1];

    private static (int, int)? WebP(ReadOnlySpan<byte> b)
    {
        var chunk = b[12..16];
        if (chunk.SequenceEqual("VP8X"u8) && b.Length >= 30)
            return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (chunk.SequenceEqual("VP8 "u8) && b.Length >= 30)
            return ((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF);
        if (chunk.SequenceEqual("VP8L"u8) && b.Length >= 25)
        {
            var v = b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24;
            return (1 + (v & 0x3FFF), 1 + ((v >> 14) & 0x3FFF));
        }
        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BE16(b[(i + 7)..]), BE16(b[(i + 5)..]));
            i += 2 + BE16(b[(i + 2)..]);
        }
        return null;
    }
}
