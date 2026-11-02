using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Video/audio preview without downloading the file first (grilled 2026-11-02). &lt;video src&gt; cannot send the
/// bearer header and a 15-minute JWT would expire mid-playback, so the client first asks for a ticket (access is
/// checked here, with the caller's token) and the media element then streams /api/media/stream?t=… with Range
/// requests: playback starts at once and seeking only fetches what is watched. NAS files are copied once to a
/// local cache because FileStation has no ranged download; the copy is evicted after a day unused.
public static class MediaStreamEndpoints
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(6);
    private const string TicketPurpose = "IPop.Media.Stream";

    public record TicketRequest(string Source, Guid? Id, string? Server, string? Path);
    public record TicketResponse(string StreamUrl, string Mime);

    /// What a ticket points at. Key is unique per readable file (Files are per user: NAS rights differ).
    internal sealed record MediaRef(string Source, string Key, Guid UserId, string FileName, string Mime);

    public static IEndpointRouteBuilder MapMediaStream(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/media/ticket", async (TicketRequest req, HttpContext http, IPopDbContext db,
            IEnumerable<IAttachmentAccessPolicy> policies, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (string.IsNullOrEmpty(nik)) return Results.Unauthorized();
            var userId = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (userId is null) return Results.Unauthorized();

            MediaRef? media = null;
            switch (req.Source)
            {
                case "chat" when req.Id is { } id:
                {
                    var a = await db.ChatAttachments.FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (a is null || !await ChatEndpoints.CanAccessAttachmentAsync(db, a, userId.Value, ct)) return NotFound();
                    media = new MediaRef("chat", id.ToString("N"), userId.Value, a.FileName, await ChatEndpoints.EnsureMimeAsync(db, a, ct));
                    break;
                }
                case "mail" when req.Id is { } id:
                {
                    var a = await db.MailAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (a is null) return NotFound();
                    var can = a.MailId is { } mailId
                        ? await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId.Value, ct)
                        : MailEndpoints.IsPendingUploadOf(a, userId.Value);
                    if (!can) return NotFound();
                    media = new MediaRef("mail", id.ToString("N"), userId.Value, a.FileName, ChatEndpoints.ResolveMime(a.FileName, a.MimeType));
                    break;
                }
                case "shared" when req.Id is { } id:
                {
                    var a = await db.Attachments.AsNoTracking().Where(x => x.Id == id && x.DeletedAt == null)
                        .Select(x => new { x.Area, x.FileName, x.ContentType }).FirstOrDefaultAsync(ct);
                    var policy = a is null ? null : policies.FirstOrDefault(p => p.Area == a.Area);
                    if (a is null || policy is null || !await policy.CanReadAsync(id, userId.Value, ct)) return NotFound();
                    media = new MediaRef("shared", id.ToString("N"), userId.Value, a.FileName, ChatEndpoints.ResolveMime(a.FileName, a.ContentType));
                    break;
                }
                case "files" when !string.IsNullOrWhiteSpace(req.Server) && !string.IsNullOrWhiteSpace(req.Path):
                {
                    // NAS rights are checked by FileStation itself with the caller's credential when the file is read.
                    var path = req.Path.Trim();
                    var name = System.IO.Path.GetFileName(path);
                    media = new MediaRef("files", $"{req.Server}|{path}", userId.Value, name, ChatEndpoints.ResolveMime(name, null));
                    break;
                }
            }
            if (media is null) return Results.BadRequest(new { code = "invalid_source" });
            if (!IsStreamable(media.Mime)) return Results.Json(new { code = "not_media", message = "Only video and audio stream." }, statusCode: 415);

            var t = Uri.EscapeDataString(Protect(dp, media));
            return Results.Ok(new TicketResponse($"/api/media/stream?t={t}", media.Mime));
        }).RequireAuthorization();

        // Ticket-authenticated: the browser's media element fetches this directly, with Range.
        app.MapMethods("/api/media/stream", ["GET", "HEAD"], async (string? t, HttpContext http, IPopDbContext db,
            IWebHostEnvironment env, IFileAccessService files, IAttachmentStore store, IConfiguration cfg,
            IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (Unprotect(dp, t) is not { } media) return Results.Unauthorized();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            http.Response.Headers.CacheControl = "private, max-age=3600";
            try
            {
                var local = await ResolveLocalAsync(media, db, env, files, store, cfg, ct);
                return local is null ? Results.NotFound() : Results.File(local, media.Mime, enableRangeProcessing: true);
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
            }
            catch (SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        }).AllowAnonymous();

        return app;
    }

    internal static bool IsStreamable(string? mime) =>
        mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    private static IResult NotFound() => Results.Json(new { code = "not_found", message = "File not found." }, statusCode: 404);

    // ── Tickets ────────────────────────────────────────────────────────────────

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) =>
        dp.CreateProtector(TicketPurpose).ToTimeLimitedDataProtector();

    internal static string Protect(IDataProtectionProvider dp, MediaRef m) =>
        Protector(dp).Protect(string.Join('\n', m.Source, m.Key, m.UserId.ToString("N"), m.FileName, m.Mime), TicketLifetime);

    internal static MediaRef? Unprotect(IDataProtectionProvider dp, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        try
        {
            var p = Protector(dp).Unprotect(ticket).Split('\n');
            if (p.Length != 5 || !Guid.TryParse(p[2], out var uid) || !IsStreamable(p[4])) return null;
            return new MediaRef(p[0], p[1], uid, p[3], p[4]);
        }
        catch (CryptographicException) { return null; }
    }

    // ── Bytes ──────────────────────────────────────────────────────────────────

    private static async Task<string?> ResolveLocalAsync(MediaRef m, IPopDbContext db, IWebHostEnvironment env,
        IFileAccessService files, IAttachmentStore store, IConfiguration cfg, CancellationToken ct)
    {
        switch (m.Source)
        {
            case "chat":
            {
                var a = await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                if (a is null) return null;
                if (ChatEndpoints.TryLocalFile(env.ContentRootPath, a.SynologyServer, a.SynologyPath, out var full)) return full;
                return await Cache(cfg).GetAsync("chat-" + m.Key, c => ChatEndpoints.DownloadAttachmentAsync(files, a.SynologyPath, c), ct);
            }
            case "mail":
            {
                var a = await db.MailAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                if (a is null) return null;
                if (a.SynologyServer == "local")
                {
                    var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
                    var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, a.SynologyPath));
                    return combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(combined) ? combined : null;
                }
                return await Cache(cfg).GetAsync("mail-" + m.Key, c =>
                    MailEndpoints.IsSharedMailPath(a.SynologyPath) && files.HasServiceAccount && a.SynologyServer == files.ServiceServer
                        ? files.DownloadAsServiceAsync(a.SynologyPath, c)
                        : files.DownloadAsync(m.UserId, a.SynologyServer, a.SynologyPath, c), ct);
            }
            case "shared":
                return await Cache(cfg).GetAsync("shared-" + m.Key, async c =>
                {
                    var opened = await store.OpenAsync(Guid.ParseExact(m.Key, "N"), c);
                    return opened?.Content ?? throw new FileNotFoundException();
                }, ct);
            case "files":
            {
                var sep = m.Key.IndexOf('|');
                var server = m.Key[..sep];
                var path = m.Key[(sep + 1)..];
                // Per user: the same NAS path can be readable for one person and not another.
                return await Cache(cfg).GetAsync($"files-{m.UserId:N}-{Hash(m.Key)}", c => files.DownloadAsync(m.UserId, server, path, c), ct);
            }
        }
        return null;
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];

    private static MediaFileCache? _cache;
    private static MediaFileCache Cache(IConfiguration cfg) =>
        _cache ??= new MediaFileCache(cfg["Media:CacheDir"] ?? Path.Combine(Path.GetTempPath(), "ipop-media-cache"));
}

/// Local copies of NAS media so Range requests are served from disk. One download per file at a time; files
/// unused for a day are removed, and the folder is kept under a size cap (oldest access first).
public sealed class MediaFileCache(string dir, long maxBytes = 20L * 1024 * 1024 * 1024)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private DateTime _lastSweep = DateTime.MinValue;

    public async Task<string?> GetAsync(string key, Func<CancellationToken, Task<Stream>> fetch, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, key + ".bin");
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
            {
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    // The copy must finish even if the first viewer closes the preview mid-way.
                    await using (var src = await fetch(CancellationToken.None))
                    await using (var dst = File.Create(tmp))
                        await src.CopyToAsync(dst, CancellationToken.None);
                    File.Move(tmp, path, overwrite: true);
                }
                catch (FileNotFoundException) { TryDelete(tmp); return null; }
                catch { TryDelete(tmp); throw; }
            }
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
        }
        finally { gate.Release(); }
        Sweep(path);
        return path;
    }

    private void Sweep(string keep)
    {
        if (DateTime.UtcNow - _lastSweep < TimeSpan.FromMinutes(10)) return;
        _lastSweep = DateTime.UtcNow;
        try
        {
            var all = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = all.Sum(f => f.Length);
            foreach (var f in all)
            {
                if (f.FullName == keep) continue;
                var stale = f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-1);
                if (!stale && total <= maxBytes) continue;
                total -= f.Length;
                TryDelete(f.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
