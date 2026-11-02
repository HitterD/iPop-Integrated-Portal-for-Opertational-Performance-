using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Video/audio preview without downloading the file first (grilled 2026-11-02). &lt;video src&gt; cannot send the
/// bearer header and a 15-minute JWT would expire mid-playback, so the client first asks for a ticket (access is
/// checked with the caller's token) and the media element then streams /api/media/stream?t=… with Range requests:
/// playback starts at once and seeking only fetches what is watched.
///   • Access is checked again on every stream request (left the room, deactivated → stops at once).
///   • Server-local files stream immediately. NAS files are copied once into a local cache in the background
///     (FileStation has no ranged download); until the copy is ready the stream answers 202 + Retry-After and the
///     player shows "Preparing video…" and tries again, so a large file never hits the proxy's read timeout.
public static class MediaStreamEndpoints
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(2);
    private const string TicketPurpose = "IPop.Media.Stream";

    public record TicketRequest(string Source, Guid? Id, string? Server, string? Path, long? Size, long? Mtime);
    public record TicketResponse(string StreamUrl, string Mime, DateTime ExpiresAt);

    /// What a ticket points at. Key is unique per readable file version (Files: per user, server, path, size, mtime).
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

            MediaRef? media = req.Source switch
            {
                "chat" when req.Id is { } id => await ChatRefAsync(db, id, userId.Value, ct),
                "mail" when req.Id is { } id => await MailRefAsync(db, id, userId.Value, ct),
                "shared" when req.Id is { } id => await SharedRefAsync(db, policies, id, userId.Value, ct),
                "files" when !string.IsNullOrWhiteSpace(req.Server) && !string.IsNullOrWhiteSpace(req.Path) =>
                    FilesRef(req.Server.Trim(), req.Path.Trim(), req.Size ?? 0, req.Mtime ?? 0, userId.Value),
                _ => null,
            };
            if (media is null)
                return req.Source is "chat" or "mail" or "shared" or "files"
                    ? Results.Json(new { code = "not_found", message = "File not found." }, statusCode: 404)
                    : Results.BadRequest(new { code = "invalid_source" });
            if (!IsStreamable(media.Mime)) return Results.Json(new { code = "not_media", message = "This file is not a video or audio file." }, statusCode: 415);

            var t = Uri.EscapeDataString(Protect(dp, media));
            return Results.Ok(new TicketResponse($"/api/media/stream?t={t}", media.Mime, DateTime.UtcNow.Add(TicketLifetime)));
        }).RequireAuthorization();

        // Ticket-authenticated: the browser's media element fetches this directly, with Range.
        app.MapMethods("/api/media/stream", ["GET", "HEAD"], async (string? t, HttpContext http, IPopDbContext db,
            IEnumerable<IAttachmentAccessPolicy> policies, IWebHostEnvironment env, IServiceScopeFactory scopes, IConfiguration cfg,
            IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (Unprotect(dp, t) is not { } media) return Results.Unauthorized();
            if (!await StillAllowedAsync(media, db, policies, ct)) return Results.Unauthorized();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";

            var local = await LocalFileAsync(media, db, env, ct);
            if (local is { Found: false }) return Results.NotFound();
            if (local is { Path: { } path })
            {
                http.Response.Headers.CacheControl = "private, max-age=600";
                return Results.File(path, media.Mime, enableRangeProcessing: true);
            }

            // NAS: serve the cached copy, or start the copy and ask the player to come back shortly.
            var cache = Cache(cfg);
            switch (cache.State(media.Key))
            {
                case CacheState.Ready(var p):
                    http.Response.Headers.CacheControl = "private, max-age=600";
                    return Results.File(p, media.Mime, enableRangeProcessing: true);
                case CacheState.Failed(var error):
                    cache.Forget(media.Key);
                    return error;
                default:
                    cache.Start(media.Key, c => OpenNasAsync(media, scopes, c));
                    http.Response.Headers.RetryAfter = "2";
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Json(new { code = "preparing", message = "Preparing the video…" }, statusCode: 202);
            }
        }).AllowAnonymous();

        return app;
    }

    internal static bool IsStreamable(string? mime) =>
        mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    // ── Who may read what (ticket time and again on every stream request) ────────────────

    private static async Task<MediaRef?> ChatRefAsync(IPopDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var a = await db.ChatAttachments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || !await ChatEndpoints.CanAccessAttachmentAsync(db, a, userId, ct)) return null;
        return new MediaRef("chat", id.ToString("N"), userId, a.FileName, await ChatEndpoints.EnsureMimeAsync(db, a, ct));
    }

    private static async Task<MediaRef?> MailRefAsync(IPopDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var a = await db.MailAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || !await CanReadMailAsync(db, a, userId, ct)) return null;
        return new MediaRef("mail", id.ToString("N"), userId, a.FileName, ChatEndpoints.ResolveMime(a.FileName, a.MimeType));
    }

    private static async Task<bool> CanReadMailAsync(IPopDbContext db, MailAttachment a, Guid userId, CancellationToken ct) =>
        a.MailId is { } mailId
            ? await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId, ct)
            : MailEndpoints.IsPendingUploadOf(a, userId);

    private static async Task<MediaRef?> SharedRefAsync(IPopDbContext db, IEnumerable<IAttachmentAccessPolicy> policies, Guid id, Guid userId, CancellationToken ct)
    {
        var a = await db.Attachments.AsNoTracking().Where(x => x.Id == id && x.DeletedAt == null)
            .Select(x => new { x.Area, x.FileName, x.ContentType }).FirstOrDefaultAsync(ct);
        var policy = a is null ? null : policies.FirstOrDefault(p => p.Area == a.Area);
        if (a is null || policy is null || !await policy.CanReadAsync(id, userId, ct)) return null;
        return new MediaRef("shared", id.ToString("N"), userId, a.FileName, ChatEndpoints.ResolveMime(a.FileName, a.ContentType));
    }

    /// NAS rights are FileStation's own: the copy is read with the person's credential, and the key carries the file's
    /// size + modified time so a replaced file is fetched again (and an old copy is never served for a new version).
    private static MediaRef FilesRef(string server, string path, long size, long mtime, Guid userId)
    {
        var name = Path.GetFileName(path);
        return new MediaRef("files", $"{server}|{path}|{size}|{mtime}", userId, name, MediaMime(name));
    }

    /// Video types the browser may or may not play; unknown ones still stream (the player says when it can't).
    internal static string MediaMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        ".3gp" => "video/3gpp",
        ".mpeg" or ".mpg" => "video/mpeg",
        ".ogv" => "video/ogg",
        _ => ChatEndpoints.ResolveMime(name, null),
    };

    private static async Task<bool> StillAllowedAsync(MediaRef m, IPopDbContext db, IEnumerable<IAttachmentAccessPolicy> policies, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == m.UserId).Select(u => new { u.IsActive, u.LockedUntil }).FirstOrDefaultAsync(ct);
        if (user is null || !user.IsActive || user.LockedUntil > DateTime.UtcNow) return false;
        return m.Source switch
        {
            "chat" => Guid.TryParseExact(m.Key, "N", out var id) && await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) is { } a
                      && await ChatEndpoints.CanAccessAttachmentAsync(db, a, m.UserId, ct),
            "mail" => Guid.TryParseExact(m.Key, "N", out var id) && await db.MailAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) is { } a
                      && await CanReadMailAsync(db, a, m.UserId, ct),
            "shared" => Guid.TryParseExact(m.Key, "N", out var id) && await SharedRefAsync(db, policies, id, m.UserId, ct) is not null,
            "files" => true, // read with the person's own NAS credential when the copy is made
            _ => false,
        };
    }

    // ── Tickets ────────────────────────────────────────────────────────────────

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp) =>
        dp.CreateProtector(TicketPurpose).ToTimeLimitedDataProtector();

    internal static string Protect(IDataProtectionProvider dp, MediaRef m) =>
        Protector(dp).Protect(string.Join('\n', m.Source, m.Key, m.UserId.ToString("N"), m.FileName.Replace('\n', ' '), m.Mime), TicketLifetime);

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

    /// Files that already sit on this server's disk: (Found, Path). Path null with Found = the file lives on the NAS.
    private static async Task<(bool Found, string? Path)?> LocalFileAsync(MediaRef m, IPopDbContext db, IWebHostEnvironment env, CancellationToken ct)
    {
        switch (m.Source)
        {
            case "chat":
            {
                var a = await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                if (a is null) return (false, null);
                return ChatEndpoints.TryLocalFile(env.ContentRootPath, a.SynologyServer, a.SynologyPath, out var full) ? (true, full) : (true, null);
            }
            case "mail":
            {
                var a = await db.MailAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                if (a is null) return (false, null);
                if (a.SynologyServer != "local") return (true, null);
                var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
                var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, a.SynologyPath));
                return combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(combined)
                    ? (true, combined) : (false, null);
            }
        }
        return null; // shared + files: always through the cache
    }

    /// Opens the NAS file in a fresh scope (the copy outlives the request that started it).
    private static async Task<Stream> OpenNasAsync(MediaRef m, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IPopDbContext>();
        var files = sp.GetRequiredService<IFileAccessService>();
        switch (m.Source)
        {
            case "chat":
            {
                var a = await db.ChatAttachments.AsNoTracking().FirstAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                return await ChatEndpoints.DownloadAttachmentAsync(files, a.SynologyPath, ct);
            }
            case "mail":
            {
                var a = await db.MailAttachments.AsNoTracking().FirstAsync(x => x.Id == Guid.ParseExact(m.Key, "N"), ct);
                return MailEndpoints.IsSharedMailPath(a.SynologyPath) && files.HasServiceAccount && a.SynologyServer == files.ServiceServer
                    ? await files.DownloadAsServiceAsync(a.SynologyPath, ct)
                    : await files.DownloadAsync(m.UserId, a.SynologyServer, a.SynologyPath, ct);
            }
            case "shared":
            {
                var opened = await sp.GetRequiredService<IAttachmentStore>().OpenAsync(Guid.ParseExact(m.Key, "N"), ct);
                return opened?.Content ?? throw new FileNotFoundException();
            }
            case "files":
            {
                var parts = m.Key.Split('|');
                return await files.DownloadAsync(m.UserId, parts[0], parts[1], ct);
            }
        }
        throw new FileNotFoundException();
    }

    private static MediaFileCache? _cache;
    private static readonly object CacheLock = new();
    internal static MediaFileCache Cache(IConfiguration cfg)
    {
        lock (CacheLock)
            return _cache ??= new MediaFileCache(cfg["Media:CacheDir"] ?? DefaultCacheDir(cfg));
    }

    /// Next to the meeting recordings cache on the data drive (F:) in production, never on C:.
    private static string DefaultCacheDir(IConfiguration cfg)
    {
        var spool = cfg["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool";
        var parent = Path.GetDirectoryName(spool.TrimEnd('\\', '/'));
        return parent is not null && Directory.Exists(parent) ? Path.Combine(parent, "media-cache") : Path.Combine(Path.GetTempPath(), "ipop-media-cache");
    }
}

/// What the cache knows about a key.
public abstract record CacheState
{
    public sealed record Missing : CacheState;
    public sealed record Copying : CacheState;
    public sealed record Ready(string Path) : CacheState;
    public sealed record Failed(IResult Error) : CacheState;
}

/// Local copies of NAS media so Range requests are served from disk. One background copy per file; unused copies are
/// removed after a day and the folder is kept under a size cap (oldest access first).
public sealed class MediaFileCache(string dir, long maxBytes = 30L * 1024 * 1024 * 1024)
{
    private readonly ConcurrentDictionary<string, Task> _copies = new();
    private readonly ConcurrentDictionary<string, IResult> _failures = new();
    private DateTime _lastSweep = DateTime.MinValue;
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(30);

    public string PathFor(string key) =>
        Path.Combine(dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40] + ".bin");

    public CacheState State(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
            Sweep(path);
            return new CacheState.Ready(path);
        }
        if (_failures.TryGetValue(key, out var error)) return new CacheState.Failed(error);
        return _copies.ContainsKey(key) ? new CacheState.Copying() : new CacheState.Missing();
    }

    public void Forget(string key) => _failures.TryRemove(key, out _);

    /// Starts the copy unless one is running. Errors are remembered once so the next player poll can show them.
    public void Start(string key, Func<CancellationToken, Task<Stream>> fetch)
    {
        _copies.GetOrAdd(key, k => Task.Run(async () =>
        {
            var path = PathFor(k);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                Directory.CreateDirectory(dir);
                using var cts = new CancellationTokenSource(CopyTimeout);
                await using (var src = await fetch(cts.Token))
                await using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                    await src.CopyToAsync(dst, 1 << 20, cts.Token);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                _failures[k] = ex switch
                {
                    SynoException { IsAuthFailure: true } s => Results.Json(new { code = "synology_credential_required", message = s.Message }, statusCode: 401),
                    FileNotFoundException => Results.NotFound(),
                    _ => Results.Json(new { code = "synology_error", message = "The file could not be read from the NAS." }, statusCode: 502),
                };
            }
            finally { _copies.TryRemove(k, out _); }
        }));
    }

    /// For tests and callers that need the file now: starts (or joins) the copy and waits for it.
    public async Task<string?> GetAsync(string key, Func<CancellationToken, Task<Stream>> fetch, CancellationToken ct)
    {
        Start(key, fetch);
        if (_copies.TryGetValue(key, out var running)) await running.WaitAsync(ct);
        return State(key) switch
        {
            CacheState.Ready r => r.Path,
            CacheState.Failed => throw new IOException("copy failed"),
            _ => null,
        };
    }

    private void Sweep(string keep)
    {
        if (DateTime.UtcNow - _lastSweep < TimeSpan.FromMinutes(10)) return;
        _lastSweep = DateTime.UtcNow;
        try
        {
            var all = new DirectoryInfo(dir).GetFiles("*.bin").OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = all.Sum(f => f.Length);
            foreach (var f in all)
            {
                if (f.FullName == keep) continue;
                var stale = f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-1);
                if (!stale && total <= maxBytes) continue;
                total -= f.Length;
                TryDelete(f.FullName); // a file being served is open without delete sharing: Delete fails, kept
            }
            // Leftovers of a crash mid-copy.
            foreach (var p in new DirectoryInfo(dir).GetFiles("*.part").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-2)))
                TryDelete(p.FullName);
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
