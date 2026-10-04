using System.Security.Cryptography;
using IPop.Application.Files;
using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.Attachments;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.News;

public sealed class NewsVideoOptions
{
    /// Absolute folder for the streaming copies (videos + posters).
    public required string LocalRoot { get; init; }
}

/// The video's News post goes through NewsService (same targeting, feed and notifications); this
/// service owns the file, the popup window and the per-user popup/play/download marks.
public sealed class NewsVideoService(
    IPopDbContext db, INewsService news, NewsVideoOptions opt,
    ISynologyGateway? gw = null, AttachmentOptions? nas = null, AttachmentSession? nasSession = null,
    ILogger<NewsVideoService>? log = null) : INewsVideoService
{
    private static readonly Dictionary<string, string> VideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".webm"] = "video/webm",
    };
    private static readonly Dictionary<string, string> PosterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
    };

    public static bool IsAllowedVideo(string fileName) => VideoTypes.ContainsKey(Path.GetExtension(fileName));

    public async Task<NewsVideoResult> UploadAsync(Guid actorId, NewsVideoUpload up, CancellationToken ct)
    {
        if (!await IsAdminAsync(actorId, ct)) return Fail("forbidden");
        if (string.IsNullOrWhiteSpace(up.Title)) return Fail("title_required");
        if (up.Title.Trim().Length > 300) return Fail("title_too_long");
        var ext = Path.GetExtension(up.FileName).ToLowerInvariant();
        if (!VideoTypes.TryGetValue(ext, out var videoType)) return Fail("blocked_type");
        var info = new FileInfo(up.VideoTempPath);
        if (!info.Exists || info.Length == 0) return Fail("empty");
        if (info.Length > NewsVideoLimits.MaxBytes) return Fail("too_large");
        if (!await LooksLikeVideoAsync(up.VideoTempPath, ext, ct)) return Fail("blocked_type");
        string? posterExt = null;
        if (up.PosterTempPath is not null)
        {
            posterExt = await SniffPosterAsync(up.PosterTempPath, ct);
            if (posterExt is null) return Fail("invalid_poster");
        }
        var now = DateTime.UtcNow;
        if (up.PopupUntil is { } until && until <= now) return Fail("popup_until_in_past");

        // Description is the post body; News requires one.
        var body = string.IsNullOrWhiteSpace(up.Description) ? up.Title.Trim() : up.Description.Trim();
        var created = await news.CreateAsync(actorId, new CreateNewsRequest(
            Type: "announcement", Title: up.Title.Trim(), Body: body, Targets: up.Targets, Mode: "publish"), ct);
        if (!created.Success || created.News is null) return Fail(created.Error ?? "create_failed");

        var id = Guid.NewGuid();
        var rel = $"{now:yyyy}/{now:MM}/{id:N}";
        var videoRel = rel + ext;
        var videoFull = Full(videoRel);
        Directory.CreateDirectory(Path.GetDirectoryName(videoFull)!);
        File.Move(up.VideoTempPath, videoFull);
        string? posterRel = null;
        if (up.PosterTempPath is not null)
        {
            posterRel = rel + "-poster" + posterExt;
            File.Move(up.PosterTempPath, Full(posterRel));
        }

        var video = new NewsVideo
        {
            Id = id, NewsPostId = created.News.Id,
            FileName = SafeName(up.FileName, ext), ContentType = videoType,
            Size = info.Length, Sha256 = await HashAsync(videoFull, ct),
            DurationSeconds = Math.Max(0, up.DurationSeconds),
            LocalPath = videoRel, ThumbnailPath = posterRel,
            PopupUntil = up.PopupUntil ?? now.AddDays(NewsVideoLimits.DefaultPopupDays),
            UploaderId = actorId, CreatedAt = now,
        };
        db.NewsVideos.Add(video);
        db.AuditLogs.Add(Audit("news.video.upload", video.Id, new { actor = actorId, post = video.NewsPostId, file = video.FileName, size = video.Size }, now));
        await db.SaveChangesAsync(ct);
        return new NewsVideoResult(true, await AdminDtoAsync(video.Id, ct), null);
    }

    /// Copies the local video to the NAS archive. Runs after the upload response (the admin is not
    /// kept waiting on a 500 MB NAS transfer); a failure leaves NasPath null and is retried on the next call.
    public async Task<bool> ArchiveAsync(Guid videoId, CancellationToken ct)
    {
        if (gw is null || nas is null || nasSession is null || !nas.HasServiceAccount) return false;
        var v = await db.NewsVideos.FirstOrDefaultAsync(x => x.Id == videoId, ct);
        if (v is null || v.NasPath is not null) return v?.NasPath is not null;
        var full = Full(v.LocalPath);
        if (!File.Exists(full)) return false;
        var dir = $"{nas.FolderFor("news")}/videos/{v.CreatedAt:yyyy}/{v.CreatedAt:MM}";
        var name = Path.GetFileName(v.LocalPath);
        try
        {
            await WithNasAsync(async sid =>
            {
                await using var fs = File.OpenRead(full);
                await gw.UploadAsync(nas.Server, sid, dir, name, fs, ct);
                return true;
            }, ct);
            v.NasPath = $"{dir}/{name}";
            await ArchivePosterAsync(v, dir, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Archiving news video {Id} to {Server}:{Dir} failed; the local copy still serves it.", videoId, nas.Server, dir);
            return false;
        }
    }

    public async Task<IReadOnlyList<NewsVideoAdminDto>> ListAsync(Guid actorId, CancellationToken ct)
    {
        if (!await IsAdminAsync(actorId, ct)) return [];
        var ids = await db.NewsVideos.AsNoTracking().OrderByDescending(v => v.CreatedAt).Select(v => v.Id).ToListAsync(ct);
        var list = new List<NewsVideoAdminDto>(ids.Count);
        foreach (var id in ids) list.Add((await AdminDtoAsync(id, ct))!);
        return list;
    }

    public async Task<NewsVideoResult> SetPopupAsync(Guid actorId, Guid videoId, DateTime? popupUntil, CancellationToken ct)
    {
        if (!await IsAdminAsync(actorId, ct)) return Fail("forbidden");
        var v = await db.NewsVideos.FirstOrDefaultAsync(x => x.Id == videoId, ct);
        if (v is null) return Fail("not_found");
        var now = DateTime.UtcNow;
        if (popupUntil is { } until && until <= now) return Fail("popup_until_in_past");
        v.PopupUntil = popupUntil;
        db.AuditLogs.Add(Audit(popupUntil is null ? "news.video.popup_off" : "news.video.popup_on", v.Id, new { actor = actorId, until = popupUntil }, now));
        await db.SaveChangesAsync(ct);
        return new NewsVideoResult(true, await AdminDtoAsync(v.Id, ct), null);
    }

    public async Task<NewsVideoResult> DeleteAsync(Guid actorId, Guid videoId, CancellationToken ct)
    {
        if (!await IsAdminAsync(actorId, ct)) return Fail("forbidden");
        var v = await db.NewsVideos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == videoId, ct);
        if (v is null) return Fail("not_found");

        // The post delete cascades to the video row and its view marks.
        var deleted = await news.DeleteAsync(actorId, v.NewsPostId, ct);
        if (!deleted.Success) return Fail(deleted.Error ?? "delete_failed");
        db.AuditLogs.Add(Audit("news.video.delete", v.Id, new { actor = actorId, post = v.NewsPostId, file = v.FileName }, DateTime.UtcNow));
        await db.SaveChangesAsync(ct);

        foreach (var rel in new[] { v.LocalPath, v.ThumbnailPath })
            if (rel is not null && SafeFull(rel) is { } f && File.Exists(f)) File.Delete(f);
        if (v.NasPath is not null && gw is not null && nas is not null && nasSession is not null)
        {
            try
            {
                await WithNasAsync(async sid => { await gw.DeleteAsync(nas.Server, sid, v.NasPath, ct); return true; }, ct);
                if (PosterNasPath(v) is { } pn) await WithNasAsync(async sid => { await gw.DeleteAsync(nas.Server, sid, pn, ct); return true; }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.LogWarning(ex, "Deleting archived news video {Path} failed; the file is orphaned on the NAS.", v.NasPath);
            }
        }
        return new NewsVideoResult(true, null, null);
    }

    public async Task<NewsVideoDto?> GetPendingPopupAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Newest live video with an open window that this user may see; one popup at a time.
        var candidates = await db.NewsVideos.AsNoTracking()
            .Where(v => v.PopupUntil != null && v.PopupUntil > now && v.NewsPost.Status == "published"
                && (v.NewsPost.PublishAt == null || v.NewsPost.PublishAt <= now))
            .OrderByDescending(v => v.CreatedAt).Select(v => v.Id).Take(5).ToListAsync(ct);
        foreach (var id in candidates)
        {
            var dto = await GetAsync(id, userId, ct);
            if (dto is null) continue;
            var seen = await db.NewsVideoViews.AsNoTracking().AnyAsync(x => x.NewsVideoId == id && x.UserId == userId && x.PopupSeenAt != null, ct);
            return seen ? null : dto;
        }
        return null;
    }

    public async Task<NewsVideoDto?> GetAsync(Guid videoId, Guid userId, CancellationToken ct)
    {
        var v = await db.NewsVideos.AsNoTracking().Include(x => x.NewsPost).ThenInclude(p => p.Author)
            .FirstOrDefaultAsync(x => x.Id == videoId, ct);
        if (v is null) return null;
        // NewsService owns visibility (status, schedule, targeting, manager override).
        if (await news.GetAsync(v.NewsPostId, userId, ct) is null) return null;
        var p = v.NewsPost;
        return new NewsVideoDto(v.Id, v.NewsPostId, p.Title, p.Body, v.FileName, v.ContentType, v.Size,
            v.DurationSeconds, v.ThumbnailPath is not null, p.PublishedAt ?? p.CreatedAt, p.Author.Name);
    }

    public async Task<bool> TrackAsync(Guid userId, Guid videoId, string kind, CancellationToken ct)
    {
        if (kind is not ("seen" or "played" or "downloaded")) return false;
        if (await GetAsync(videoId, userId, ct) is null) return false;
        var row = await db.NewsVideoViews.FirstOrDefaultAsync(x => x.NewsVideoId == videoId && x.UserId == userId, ct);
        if (row is null)
        {
            row = new NewsVideoView { NewsVideoId = videoId, UserId = userId };
            db.NewsVideoViews.Add(row);
        }
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case "seen": row.PopupSeenAt ??= now; break;
            case "played": row.PlayedAt ??= now; break;
            case "downloaded": row.DownloadedAt ??= now; break;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* a parallel mark created the row first; the first time wins */ }
        return true;
    }

    public async Task<NewsVideoFile?> OpenFileAsync(Guid videoId, bool poster, CancellationToken ct)
    {
        var v = await db.NewsVideos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == videoId, ct);
        if (v is null) return null;
        if (poster)
        {
            if (v.ThumbnailPath is null || SafeFull(v.ThumbnailPath) is not { } pf) return null;
            if (!File.Exists(pf) && (PosterNasPath(v) is not { } pn || !await RestoreFromNasAsync(v, pn, pf, ct))) return null;
            return new NewsVideoFile(pf, PosterTypes.GetValueOrDefault(Path.GetExtension(pf), "image/jpeg"), Path.GetFileName(pf));
        }
        var full = SafeFull(v.LocalPath);
        if (full is null) return null;
        if (!File.Exists(full) && (v.NasPath is null || !await RestoreFromNasAsync(v, v.NasPath, full, ct))) return null;
        return new NewsVideoFile(full, v.ContentType, v.FileName);
    }

    /// The local copy is a cache of the NAS archive: bring it back when the API disk lost it.
    private async Task<bool> RestoreFromNasAsync(NewsVideo v, string nasPath, string full, CancellationToken ct)
    {
        if (gw is null || nas is null || nasSession is null) return false;
        var tmp = full + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await WithNasAsync(async sid =>
            {
                await using var src = await gw.DownloadAsync(nas.Server, sid, nasPath, ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
                return true;
            }, ct);
            File.Move(tmp, full, overwrite: true);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Restoring news video {Id} from {Path} failed.", v.Id, nasPath);
            if (File.Exists(tmp)) File.Delete(tmp);
            return false;
        }
    }

    private async Task<NewsVideoAdminDto?> AdminDtoAsync(Guid id, CancellationToken ct)
    {
        var v = await db.NewsVideos.AsNoTracking().Include(x => x.NewsPost).ThenInclude(p => p.Targets)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        var views = db.NewsVideoViews.AsNoTracking().Where(x => x.NewsVideoId == id);
        return new NewsVideoAdminDto(v.Id, v.NewsPostId, v.NewsPost.Title, v.FileName, v.Size, v.DurationSeconds,
            v.ThumbnailPath is not null, v.NasPath is not null, v.CreatedAt, v.PopupUntil,
            v.PopupUntil is { } u && u > DateTime.UtcNow,
            v.NewsPost.Targets.Select(t => new NewsTargetDto(t.Kind, t.Value)).ToList(),
            await views.CountAsync(x => x.PopupSeenAt != null, ct),
            await views.CountAsync(x => x.PlayedAt != null, ct),
            await views.CountAsync(x => x.DownloadedAt != null, ct));
    }

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct) =>
        await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    /// The poster is archived next to the video under its local file name, so its NAS path is derived
    /// rather than stored (no schema change).
    internal static string? PosterNasPath(NewsVideo v) =>
        v.NasPath is null || v.ThumbnailPath is null ? null
        : $"{v.NasPath[..(v.NasPath.LastIndexOf('/') + 1)]}{Path.GetFileName(v.ThumbnailPath)}";

    /// Best effort: a poster that fails to archive only means no NAS copy; the video archive still counts.
    private async Task ArchivePosterAsync(NewsVideo v, string dir, CancellationToken ct)
    {
        if (v.ThumbnailPath is null || SafeFull(v.ThumbnailPath) is not { } pf || !File.Exists(pf)) return;
        try
        {
            await WithNasAsync(async sid =>
            {
                await using var fs = File.OpenRead(pf);
                await gw!.UploadAsync(nas!.Server, sid, dir, Path.GetFileName(pf), fs, ct);
                return true;
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Archiving the poster of news video {Id} failed; the local copy still serves it.", v.Id);
        }
    }

    /// Videos archived before posters were: copy their posters to the NAS too. Returns how many were copied.
    public async Task<int> ArchiveMissingPostersAsync(CancellationToken ct)
    {
        if (gw is null || nas is null || nasSession is null || !nas.HasServiceAccount) return 0;
        var list = await db.NewsVideos.AsNoTracking().Where(v => v.NasPath != null && v.ThumbnailPath != null).ToListAsync(ct);
        var n = 0;
        foreach (var v in list)
        {
            if (SafeFull(v.ThumbnailPath!) is not { } pf || !File.Exists(pf)) continue;
            await ArchivePosterAsync(v, v.NasPath![..v.NasPath!.LastIndexOf('/')], ct);
            n++;
        }
        return n;
    }

    private async Task<T> WithNasAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var sid = await nasSession!.GetAsync(gw!, nas!, refresh: false, ct);
        try { return await call(sid); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            sid = await nasSession.GetAsync(gw!, nas!, refresh: true, ct);
            return await call(sid);
        }
    }

    private string Full(string rel) => SafeFull(rel) ?? throw new InvalidOperationException("Video path escapes the store root.");

    private string? SafeFull(string rel)
    {
        var root = Path.GetFullPath(opt.LocalRoot);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    /// MP4 has an "ftyp" box at offset 4; WebM starts with the EBML magic.
    private static async Task<bool> LooksLikeVideoAsync(string path, string ext, CancellationToken ct)
    {
        var head = await ReadHeadAsync(path, 12, ct);
        if (ext == ".webm") return head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
        return head.Length >= 8 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p';
    }

    private static async Task<string?> SniffPosterAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length is 0 or > NewsVideoLimits.MaxPosterBytes) return null;
        var h = await ReadHeadAsync(path, 12, ct);
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h.Length >= 4 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return ".png";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        return null;
    }

    private static async Task<byte[]> ReadHeadAsync(string path, int n, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var buf = new byte[n];
        var read = await fs.ReadAtLeastAsync(buf, n, throwOnEndOfStream: false, ct);
        return buf[..read];
    }

    private static string SafeName(string fileName, string ext)
    {
        var n = Path.GetFileName(fileName).Trim();
        if (n.Length == 0) return "video" + ext;
        return n.Length <= 255 ? n : n[..(255 - ext.Length)] + ext;
    }

    private static AuditLog Audit(string action, Guid id, object detail, DateTime now) => new()
    {
        Id = Guid.NewGuid(), Action = action, Entity = "NewsVideo", EntityId = id.ToString(),
        DetailJson = System.Text.Json.JsonSerializer.Serialize(detail), CreatedAt = now,
    };

    private static NewsVideoResult Fail(string code) => new(false, null, code);
}
