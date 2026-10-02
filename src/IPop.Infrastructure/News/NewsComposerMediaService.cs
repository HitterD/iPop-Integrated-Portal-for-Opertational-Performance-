using System.Security.Cryptography;
using IPop.Application.Attachments;
using IPop.Application.Files;
using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.Attachments;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.News;

/// <summary>
/// Composer media for the full-page News editor (see INewsComposerMediaService).
///
/// <para>Storage split mirrors the rest of News: images and regular files reuse the shared "news"
/// attachment store (NAS service account, local fallback, the existing news read policy), while
/// videos are too large for that path and get their own local streaming copy plus a NAS archive —
/// the same local-stream/restore technique NewsVideoService uses, without ever granting access to
/// the admin video uploads.</para>
///
/// <para><b>Authorization.</b> Nothing here trusts a stored URL. An asset is readable only while it
/// is reachable from a draft the actor can edit or a post the actor can see, re-evaluated on every
/// request against the live database, with the user's active flag re-checked each time.</para>
/// </summary>
public sealed class NewsComposerMediaService(
    IPopDbContext db,
    INewsService news,
    IAttachmentStore store,
    NewsComposerMediaOptions opt,
    ISynologyGateway? gw = null,
    AttachmentOptions? nas = null,
    AttachmentSession? nasSession = null,
    ILogger<NewsComposerMediaService>? log = null) : INewsComposerMediaService
{
    public const string KindImage = "image";
    public const string KindVideo = "video";
    public const string KindFile = "file";

    public const string ContextDraft = "draft";
    public const string ContextPost = "post";

    private const string ActiveDraft = "active";
    private const string StateDiscarded = "discarded";
    private const string StatePromoted = "promoted";

    /// Draft states that no longer hold media alive. Everything else (active, scheduled, submitted,
    /// and any future in-flight state) does. A deny-list rather than an allow-list, so a terminal
    /// draft can never pin an asset forever and a new in-flight state is safe by default.
    private static readonly string[] TerminalDraftStates = [StateDiscarded, StatePromoted];

    // ──────────────────────────────────────────────────────────────────────
    // Upload
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsComposerMediaResult> UploadAsync(Guid actorId, NewsComposerMediaUpload upload, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return Fail("forbidden");

        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == upload.DraftId, ct);
        // Drafts are private to their author, so a stranger cannot attach media to someone else's work.
        if (draft is null || draft.OwnerId != actorId) return Fail("not_found");
        // The draft must still be an authorized edit context *now*: owner, still entitled, still active.
        var authErr = await AuthorizeDraftAsync(draft, actorId, ct);
        if (authErr is not null) return Fail(authErr);

        var kind = NormalizeKind(upload.Kind);
        if (kind is null) return Fail("invalid_kind");

        var info = new FileInfo(upload.TempPath);
        if (!info.Exists || info.Length == 0) return Fail("empty");

        return kind switch
        {
            KindVideo => await UploadVideoAsync(draft, actorId, upload, info, ct),
            KindFile => await UploadFileAsync(draft, actorId, upload, info, ct),
            _ => await UploadImageAsync(draft, actorId, upload, info, ct),
        };
    }

    public async Task<string?> AuthorizeUploadAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return "forbidden";
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null || draft.OwnerId != actorId) return "not_found";
        return await AuthorizeDraftAsync(draft, actorId, ct);
    }

    private async Task<NewsComposerMediaResult> UploadImageAsync(
        NewsComposerDraft draft, Guid actorId, NewsComposerMediaUpload upload, FileInfo info, CancellationToken ct)
    {
        if (info.Length > NewsComposerMediaLimits.MaxImageBytes) return Fail("too_large");
        var ext = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (!NewsComposerMediaLimits.ImageExtensions.Contains(ext)) return Fail("blocked_type");
        if (await SniffImageAsync(upload.TempPath, ct) is not { } sniffed) return Fail("blocked_type");
        if (sniffed != ext && !(sniffed == ".jpg" && ext == ".jpeg")) return Fail("blocked_type");

        StoredAttachment stored;
        try
        {
            await using var src = File.OpenRead(upload.TempPath);
            // The shared store enforces the 25 MB area limit and blocks executables; we already
            // applied the tighter 5 MB image guard above. The MIME comes from the sniffed magic
            // number, never the client-supplied ContentType.
            stored = await store.PutAsync(AttachmentAreas.News, upload.FileName, ImageMime(sniffed), src, actorId, ct);
        }
        catch (AttachmentRejectedException ex) { return Fail(ex.Code); }
        catch (SynoException ex)
        {
            log?.LogWarning(ex, "Storing composer image for draft {Draft} failed.", draft.Id);
            return Fail("storage_unavailable");
        }

        var media = NewMedia(draft.Id, actorId, KindImage, ImageMime(sniffed), upload, info, stored.Sha256);
        media.AttachmentId = stored.Id;
        db.NewsComposerMedia.Add(media);
        await db.SaveChangesAsync(ct);
        await AuditAsync("news.composer.media.upload", media.Id, actorId, new { kind = KindImage, draft = draft.Id, size = media.Size }, ct);

        return Ok(media, ContextDraft, draft.Id);
    }

    private async Task<NewsComposerMediaResult> UploadFileAsync(
        NewsComposerDraft draft, Guid actorId, NewsComposerMediaUpload upload, FileInfo info, CancellationToken ct)
    {
        var ext = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (!NewsComposerMediaLimits.FileExtensions.Contains(ext)) return Fail("blocked_type");

        StoredAttachment stored;
        try
        {
            await using var src = File.OpenRead(upload.TempPath);
            // The stored MIME is a canonical value derived from a short allow-listed script set; the
            // extension is already restricted to the office/document list above.
            stored = await store.PutAsync(AttachmentAreas.News, upload.FileName, FileMime(ext), src, actorId, ct);
        }
        catch (AttachmentRejectedException ex) { return Fail(ex.Code); }
        catch (SynoException ex)
        {
            log?.LogWarning(ex, "Storing composer file for draft {Draft} failed.", draft.Id);
            return Fail("storage_unavailable");
        }

        var media = NewMedia(draft.Id, actorId, KindFile, FileMime(ext), upload, info, stored.Sha256);
        media.AttachmentId = stored.Id;
        db.NewsComposerMedia.Add(media);
        await db.SaveChangesAsync(ct);
        await AuditAsync("news.composer.media.upload", media.Id, actorId, new { kind = KindFile, draft = draft.Id, size = media.Size }, ct);

        return Ok(media, ContextDraft, draft.Id);
    }

    private async Task<NewsComposerMediaResult> UploadVideoAsync(
        NewsComposerDraft draft, Guid actorId, NewsComposerMediaUpload upload, FileInfo info, CancellationToken ct)
    {
        if (info.Length > NewsComposerMediaLimits.MaxVideoBytes) return Fail("too_large");
        var ext = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (!NewsComposerMediaLimits.VideoExtensions.Contains(ext)) return Fail("blocked_type");
        if (!await LooksLikeVideoAsync(upload.TempPath, ext, ct)) return Fail("blocked_type");

        // One video per draft (and per live post it revises): refuse a second one before storing it,
        // so a 100 MB upload is not written just to be rejected.
        if (await DraftHasVideoAsync(draft, ct)) return Fail("video_limit");

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var rel = $"{now:yyyy}/{now:MM}/{id:N}{ext}";
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try { File.Move(upload.TempPath, full); }
        catch (IOException ex)
        {
            log?.LogWarning(ex, "Moving composer video for draft {Draft} failed.", draft.Id);
            return Fail("storage_unavailable");
        }

        var media = NewMedia(draft.Id, actorId, KindVideo, VideoMime(ext), upload, info, await HashAsync(full, ct));
        media.Id = id;
        media.LocalPath = rel;
        media.DurationSeconds = Math.Max(0, upload.DurationSeconds);
        db.NewsComposerMedia.Add(media);
        await db.SaveChangesAsync(ct);
        await AuditAsync("news.composer.media.upload", media.Id, actorId, new { kind = KindVideo, draft = draft.Id, size = media.Size }, ct);

        return Ok(media, ContextDraft, draft.Id);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Authorized read + ticket re-check
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsComposerMediaResult?> GetForDraftAsync(Guid actorId, Guid draftId, Guid mediaId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return null;
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null || draft.OwnerId != actorId) return null;
        if (await AuthorizeDraftAsync(draft, actorId, ct) is not null) return null;
        if (!await DraftCanReadMediaAsync(draft, mediaId, ct)) return null;

        var media = await db.NewsComposerMedia.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaId && m.DeletedAt == null, ct);
        return media is null ? null : Ok(media, ContextDraft, draftId);
    }

    public async Task<IReadOnlyList<NewsComposerMediaDto>?> ListDraftMediaAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return null;
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null || draft.OwnerId != actorId) return null;
        if (await AuthorizeDraftAsync(draft, actorId, ct) is not null) return null;

        var referenced = db.NewsComposerDraftMedia.AsNoTracking()
            .Where(r => r.DraftId == draftId).Select(r => r.MediaId);
        // Uploaded-but-not-yet-saved assets: scoped to this exact draft through UploadDraftId, which
        // the upload sets and the CAS autosave never touches. This is what keeps media the editor is
        // still showing alive across the first autosave, without an upload writing content ref rows.
        var rows = await db.NewsComposerMedia.AsNoTracking()
            .Where(m => m.DeletedAt == null && (referenced.Contains(m.Id) || m.UploadDraftId == draftId))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(m => Dto(m, ContextDraft, draftId)).ToList();
    }

    public async Task<IReadOnlyList<NewsComposerMediaDto>?> ListPostMediaAsync(Guid actorId, Guid postId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return null;
        // NewsService owns post visibility (status, schedule, targeting, author/manager override).
        if (await news.GetAsync(postId, actorId, ct) is null) return null;

        var rows = await db.NewsPostMedia.AsNoTracking()
            .Where(r => r.NewsPostId == postId)
            .Join(db.NewsComposerMedia.AsNoTracking().Where(m => m.DeletedAt == null),
                r => r.MediaId, m => m.Id, (r, m) => m)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(m => Dto(m, ContextPost, postId)).ToList();
    }

    public async Task<NewsComposerMediaFile?> OpenAsync(
        Guid mediaId, Guid userId, string contextKind, Guid contextId, bool poster, CancellationToken ct)
    {
        // Re-check the user first: a deactivated account loses access even with a valid ticket.
        if (!await IsActiveUserAsync(userId, ct)) return null;

        var media = await db.NewsComposerMedia.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mediaId && m.DeletedAt == null, ct);
        if (media is null) return null;

        // The ticket names a context; access is decided by the *current* reference graph, so an
        // audience change, a discarded draft or a re-linked asset closes the door immediately.
        if (!await ContextCanReadAsync(media, userId, contextKind, contextId, ct)) return null;
        if (poster && media.Kind != KindVideo) return null;

        return media.Kind switch
        {
            KindVideo => await OpenVideoAsync(media, poster, ct),
            KindFile when media.AttachmentId is { } aid => await OpenStoredAsync(aid, ct),
            _ => media.AttachmentId is { } imageId ? await OpenStoredAsync(imageId, ct) : null,
        };
    }

    private async Task<NewsComposerMediaFile?> OpenStoredAsync(Guid attachmentId, CancellationToken ct)
    {
        try
        {
            var opened = await store.OpenAsync(attachmentId, ct);
            return opened is null ? null : new NewsComposerMediaFile(null, opened.Value.Content, opened.Value.Meta.ContentType, opened.Value.Meta.FileName);
        }
        catch (SynoException ex)
        {
            log?.LogWarning(ex, "Opening stored composer attachment {Id} failed.", attachmentId);
            return null;
        }
    }

    private async Task<NewsComposerMediaFile?> OpenVideoAsync(NewsComposerMedia media, bool poster, CancellationToken ct)
    {
        if (poster)
        {
            if (media.ThumbnailPath is null) return null;
            var pf = SafeFull(media.ThumbnailPath);
            if (pf is null || !File.Exists(pf)) return null;
            return new NewsComposerMediaFile(pf, null, PosterContentType(pf), Path.GetFileName(pf));
        }
        if (media.LocalPath is null) return null;
        var full = SafeFull(media.LocalPath);
        if (full is null) return null;
        if (!File.Exists(full) && !await RestoreFromNasAsync(media, full, ct)) return null;
        return new NewsComposerMediaFile(full, null, media.ContentType, media.FileName);
    }

    /// The ticket's context no longer authorizes this asset: a draft must still be an editable
    /// context for that media and still reference it (or be its unpromoted owner), and a post must
    /// still be visible *and* still reference it (or be its promoted home).
    private async Task<bool> ContextCanReadAsync(NewsComposerMedia media, Guid userId, string contextKind, Guid contextId, CancellationToken ct)
    {
        if (contextKind == ContextDraft)
        {
            var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == contextId, ct);
            if (draft is null || draft.OwnerId != userId) return false;
            if (await AuthorizeDraftAsync(draft, userId, ct) is not null) return false;
            if (await DraftCanReadMediaAsync(draft, media.Id, ct)) return true;
            // The original upload stays readable through its own draft scope until promotion.
            return media.UploadDraftId == draft.Id;
        }

        if (contextKind == ContextPost)
        {
            if (await news.GetAsync(contextId, userId, ct) is null) return false;
            if (await db.NewsPostMedia.AsNoTracking().AnyAsync(r => r.NewsPostId == contextId && r.MediaId == media.Id, ct))
                return true;
            // Promotion writes the post rows; the author's ticket keeps working in the same window.
            return media.OwnerId == userId;
        }

        return false;
    }

    /// The draft editor's read union: rows the autosave wrote, plus the assets uploaded into *this*
    /// draft that no autosave has referenced yet. Never another draft's uploads.
    private async Task<bool> DraftCanReadMediaAsync(NewsComposerDraft draft, Guid mediaId, CancellationToken ct)
    {
        if (await db.NewsComposerDraftMedia.AsNoTracking()
                .AnyAsync(r => r.DraftId == draft.Id && r.MediaId == mediaId, ct)) return true;
        return await db.NewsComposerMedia.AsNoTracking()
            .AnyAsync(m => m.Id == mediaId && m.DeletedAt == null && m.UploadDraftId == draft.Id, ct);
    }

    // ──────────────────────────────────────────────────────────────────────
    // NAS archive (best effort, after the response)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<bool> ArchiveVideoAsync(Guid mediaId, CancellationToken ct)
    {
        if (gw is null || nas is null || nasSession is null || !nas.HasServiceAccount) return false;
        var m = await db.NewsComposerMedia.FirstOrDefaultAsync(x => x.Id == mediaId && x.Kind == KindVideo, ct);
        if (m is null) return false;
        if (m.NasPath is not null) return true;
        if (m.LocalPath is null) return false;
        var full = SafeFull(m.LocalPath);
        if (full is null || !File.Exists(full)) return false;

        var dir = $"{nas.FolderFor(AttachmentAreas.News)}/videos/{m.CreatedAt:yyyy}/{m.CreatedAt:MM}";
        var name = Path.GetFileName(m.LocalPath);
        try
        {
            await WithNasAsync(async sid =>
            {
                await using var fs = File.OpenRead(full);
                await gw.UploadAsync(nas.Server, sid, dir, name, fs, ct);
                return true;
            }, ct);
            m.NasPath = $"{dir}/{name}";
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Archiving composer video {Id} to {Server}:{Dir} failed; the local copy still serves it.", mediaId, nas.Server, dir);
            return false;
        }
    }

    /// The local copy is a cache of the NAS archive: fetch it back when the API disk lost it.
    private async Task<bool> RestoreFromNasAsync(NewsComposerMedia media, string full, CancellationToken ct)
    {
        if (media.NasPath is null || gw is null || nas is null || nasSession is null) return false;
        var tmp = full + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await WithNasAsync(async sid =>
            {
                await using var src = await gw.DownloadAsync(nas.Server, sid, media.NasPath, ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
                return true;
            }, ct);
            File.Move(tmp, full, overwrite: true);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Restoring composer video {Id} from {Path} failed.", media.Id, media.NasPath);
            if (File.Exists(tmp)) File.Delete(tmp);
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cleanup (conservative: only unreferenced, older than the grace window)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsComposerMediaCleanupResult> CleanupAsync(NewsComposerMediaCleanupRequest req, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - (req.MinAge > TimeSpan.Zero ? req.MinAge : NewsComposerMediaLimits.UnreferencedTtl);
        // Eligibility is applied BEFORE Take: filtering referenced rows out afterwards would let the
        // oldest referenced assets fill every batch and starve the orphans behind them.
        // Composer media and bare "news" attachments are the same kind of orphan, so they share one
        // age-ordered candidate list and one batch budget.
        var candidates = await EligibleMediaAsync(cutoff)
            .Select(m => new { m.Id, m.CreatedAt })
            .Concat(EligibleBareAttachmentsAsync(cutoff, req.IncludeBareAttachments).Select(a => new { a.Id, a.CreatedAt }))
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Max(1, req.BatchSize))
            .Select(x => x.Id)
            .ToListAsync(ct);

        var scanned = 0;
        var deleted = 0;
        var retained = 0;
        var failed = 0;

        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            try
            {
                // A candidate id is either a composer media row or a bare attachment; resolve which.
                if (await db.NewsComposerMedia.AsNoTracking().AnyAsync(m => m.Id == id, ct))
                {
                    if (req.DryRun)
                    {
                        if (await IsReferencedAsync(id, ct)) retained++; else deleted++;
                        continue;
                    }

                    // Tombstone and reference-check share one Serializable snapshot (see
                    // TombstoneWithRetryAsync), so a save or promotion that commits its ref first wins
                    // and the asset survives. Only after the tombstone is ours do we free bytes.
                    if (!await TombstoneWithRetryAsync(id, ct)) { retained++; continue; }

                    var media = await db.NewsComposerMedia.AsNoTracking().FirstAsync(m => m.Id == id, ct);
                    await FreeBytesAsync(media, ct);
                    deleted++;
                    continue;
                }

                if (req.DryRun)
                {
                    if (await IsBareAttachmentReferencedAsync(id, ct)) retained++; else deleted++;
                    continue;
                }

                if (await ReclaimBareAttachmentAsync(id, ct)) deleted++; else retained++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                log?.LogWarning(ex, "Deleting unreferenced composer media {Id} failed; it stays for the next pass.", id);
            }
        }

        return new NewsComposerMediaCleanupResult(scanned, deleted, retained, failed);
    }

    /// <summary>
    /// Media older than the grace window with no durable reference. The <c>UploadDraftId</c> upload
    /// scope is deliberately NOT a term here: it is a *transient* window (already bounded by the
    /// CreatedAt cutoff) for an asset uploaded but not yet autosaved, not a permanent lock, so an
    /// asset detached from a draft is freed on the next pass rather than held by a stale upload scope.
    /// </summary>
    private IQueryable<NewsComposerMedia> EligibleMediaAsync(DateTime cutoff) => db.NewsComposerMedia
        .Where(m => m.DeletedAt == null && m.CreatedAt < cutoff)
        .Where(m => !db.NewsPostMedia.Any(r => r.MediaId == m.Id))
        .Where(m => !db.NewsPosts.Any(p => p.CoverMediaId == m.Id))
        .Where(m => !db.NewsComposerDraftMedia.Any(r => r.MediaId == m.Id && r.Draft.State != StateDiscarded && r.Draft.State != StatePromoted));

    /// <summary>
    /// Atomically mark an unreferenced asset deleted, retried on a Postgres serialization failure.
    ///
    /// <para>The reference predicates and the tombstone run in one Serializable transaction. A single
    /// conditional statement is NOT enough: under READ COMMITTED a concurrent autosave can insert its
    /// <c>NewsComposerDraftMedia</c> row after the tombstone's snapshot but before it commits — write
    /// skew — committing a live reference to a tombstoned asset. Serializable makes one side abort
    /// with 40001; we retry, and the fresh snapshot then sees the reference.</para>
    ///
    /// <para><b>Dependency (integration gap):</b> closing the skew completely requires the writer
    /// side — the draft save that inserts refs — to be Serializable too. On SQLite (tests) the
    /// provider is serializable by default. If the draft save stays at READ COMMITTED, the residual
    /// window is a save that read <c>DeletedAt IS NULL</c> before the tombstone and commits after, and
    /// the asset would be freed under that reference. Confirm with the draft-service owner.</para>
    /// </summary>
    private async Task<bool> TombstoneWithRetryAsync(Guid mediaId, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var tx = await BeginTombstoneTxAsync(ct);
                var won = await TryTombstoneAsync(mediaId, ct);
                await tx.CommitAsync(ct);
                return won;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSerializationFailure(ex))
            {
                // A concurrent writer beat us: re-read on a fresh snapshot so its ref is visible.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTombstoneTxAsync(CancellationToken ct)
    {
        // Npgsql honours an explicit Serializable level; SQLite is serializable by default.
        var npgsql = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        return npgsql
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            : await db.Database.BeginTransactionAsync(ct);
    }

    /// Postgres serialization failures surface as PostgresException SqlState 40001, usually wrapped.
    /// Matched by shape (type name + SqlState) so this file needs no direct Npgsql reference.
    private static bool IsSerializationFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name is not "PostgresException") continue;
            var state = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
            if (state is "40001" or "40P01") return true;
        }
        return false;
    }

    /// The durable-reference predicates, shared by the tombstone. Not referenced by any live post
    /// (inline or cover) and not referenced by a draft in any non-discarded state (a test against
    /// "active" alone would free a scheduled revision's media). Used for dry runs; the delete path
    /// uses TryTombstoneAsync, which folds the same predicates into the update statement.
    private async Task<bool> IsReferencedAsync(Guid mediaId, CancellationToken ct)
    {
        if (await db.NewsPostMedia.AsNoTracking().AnyAsync(r => r.MediaId == mediaId, ct)) return true;
        if (await db.NewsPosts.AsNoTracking().AnyAsync(p => p.CoverMediaId == mediaId, ct)) return true;
        return await db.NewsComposerDraftMedia.AsNoTracking()
            .AnyAsync(r => r.MediaId == mediaId && r.Draft.State != StateDiscarded && r.Draft.State != StatePromoted, ct);
    }

    private async Task<bool> TryTombstoneAsync(Guid mediaId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var won = await db.NewsComposerMedia
            .Where(m => m.Id == mediaId && m.DeletedAt == null)
            .Where(m => !db.NewsPostMedia.Any(r => r.MediaId == m.Id))
            .Where(m => !db.NewsPosts.Any(p => p.CoverMediaId == m.Id))
            .Where(m => !db.NewsComposerDraftMedia.Any(r => r.MediaId == m.Id && r.Draft.State != StateDiscarded && r.Draft.State != StatePromoted))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, (DateTime?)now), ct);
        return won > 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Bare "news" attachments (legacy POST /api/news/attachments uploads)
    // ──────────────────────────────────────────────────────────────────────
    //
    // Those uploads create an Attachment row with Area="news" and no NewsComposerMedia row, so the
    // media GC above cannot see them. They are the same kind of orphan (uploaded, then never linked)
    // and would otherwise sit in the store forever. Only TODAY's unlinked rows are reclaimed; rows
    // already soft-deleted by the legacy ReplaceAttachmentsAsync path are left alone (see Reclaim).

    /// <summary>Old "news" attachments with no composer media row, not linked to a post or a draft.</summary>
    private IQueryable<Attachment> EligibleBareAttachmentsAsync(DateTime cutoff, bool include) => include
        ? db.Attachments
            .Where(a => a.Area == AttachmentAreas.News && a.DeletedAt == null && a.CreatedAt < cutoff)
            .Where(a => !db.NewsAttachments.Any(na => na.AttachmentId == a.Id))
            .Where(a => !db.NewsComposerDraftAttachments.Any(da => da.AttachmentId == a.Id))
            .Where(a => !db.NewsComposerMedia.Any(m => m.AttachmentId == a.Id && m.DeletedAt == null))
        : db.Attachments.Where(a => false);

    /// <summary>The durable-reference predicates for a bare attachment (dry-run diagnostics).</summary>
    private async Task<bool> IsBareAttachmentReferencedAsync(Guid attachmentId, CancellationToken ct)
    {
        if (await db.NewsAttachments.AsNoTracking().AnyAsync(na => na.AttachmentId == attachmentId, ct)) return true;
        if (await db.NewsComposerDraftAttachments.AsNoTracking().AnyAsync(da => da.AttachmentId == attachmentId, ct)) return true;
        return await db.NewsComposerMedia.AsNoTracking()
            .AnyAsync(m => m.AttachmentId == attachmentId && m.DeletedAt == null, ct);
    }

    /// <summary>
    /// Reclaim an unlinked "news" attachment. The reference re-check and the soft-delete are one
    /// conditional statement inside a Serializable transaction (same write-skew reasoning as
    /// <see cref="TombstoneWithRetryAsync"/>), then the bytes are freed through the store.
    ///
    /// <para>Deliberately no standalone pre-tombstone here: <see cref="IAttachmentStore.DeleteAsync"/>
    /// soft-deletes the row *itself* and only then removes the file, so pre-tombstoning would make it a
    /// no-op and leave the file orphaned. A legacy row already soft-deleted (DeletedAt != null) is not
    /// eligible at all and is never retried — this does not widen the known legacy cleanup gap.</para>
    /// </summary>
    private async Task<bool> ReclaimBareAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                bool won;
                await using (var tx = await BeginTombstoneTxAsync(ct))
                {
                    won = await db.Attachments
                        .Where(a => a.Id == attachmentId && a.Area == AttachmentAreas.News && a.DeletedAt == null)
                        .Where(a => !db.NewsAttachments.Any(na => na.AttachmentId == a.Id))
                        .Where(a => !db.NewsComposerDraftAttachments.Any(da => da.AttachmentId == a.Id))
                        .Where(a => !db.NewsComposerMedia.Any(m => m.AttachmentId == a.Id && m.DeletedAt == null))
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTime?)DateTime.UtcNow), ct) > 0;
                    await tx.CommitAsync(ct);
                }
                if (!won) return false;

                // The row is already soft-deleted; DeleteAsync re-checks (no-op on the row) and removes
                // the stored file — the exact path the rest of the app uses to free attachment bytes.
                try { await store.DeleteAsync(attachmentId, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log?.LogWarning(ex, "Deleting stored bare news attachment {Id} failed; it stays orphaned.", attachmentId);
                }
                return true;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSerializationFailure(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// Frees the bytes behind a tombstoned asset. The row is already the authority, so a failure here
    /// leaves an orphan file (harmless) rather than a live row pointing at a missing file.
    private async Task FreeBytesAsync(NewsComposerMedia media, CancellationToken ct)
    {
        // The FK cascade does not fire for a soft delete, so clear the media join rows explicitly.
        await db.NewsComposerDraftMedia.Where(r => r.MediaId == media.Id).ExecuteDeleteAsync(ct);

        if (media.Kind == KindVideo)
        {
            foreach (var rel in new[] { media.LocalPath, media.ThumbnailPath })
                if (rel is not null && SafeFull(rel) is { } f && File.Exists(f)) File.Delete(f);
            if (media.NasPath is not null && gw is not null && nas is not null && nasSession is not null)
            {
                try { await WithNasAsync(async sid => { await gw.DeleteAsync(nas.Server, sid, media.NasPath, ct); return true; }, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log?.LogWarning(ex, "Deleting archived composer video {Path} failed; the file is orphaned on the NAS.", media.NasPath);
                }
            }
            return;
        }

        if (media.AttachmentId is not { } aid) return;

        // Never free bytes another live link still needs: a promoted post attachment, a *regular*
        // draft attachment, or another live composer media row all keep the shared blob alive.
        var stillLinked = await db.NewsAttachments.AsNoTracking().AnyAsync(a => a.AttachmentId == aid, ct)
            || await db.NewsComposerDraftAttachments.AsNoTracking().AnyAsync(r => r.AttachmentId == aid, ct)
            || await db.NewsComposerMedia.AsNoTracking().AnyAsync(m => m.AttachmentId == aid && m.DeletedAt == null, ct);
        if (stillLinked) return;

        await db.NewsComposerMedia.Where(m => m.Id == media.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AttachmentId, (Guid?)null), ct);
        // DeleteAsync soft-deletes the row then removes the file; a no-op when already soft-deleted.
        try { await store.DeleteAsync(aid, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Deleting stored composer attachment {Id} failed; it stays orphaned.", aid);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Authorization helpers
    // ──────────────────────────────────────────────────────────────────────

    /// A draft is an authorized edit context only while its owner is active and still entitled:
    /// a *new* draft needs the HR/Admin create gate, a *revision* draft is re-authorized against the
    /// live post. Same rule the draft service applies, evaluated fresh on every request.
    private async Task<string?> AuthorizeDraftAsync(NewsComposerDraft draft, Guid actorId, CancellationToken ct)
    {
        if (draft.State != ActiveDraft) return "not_active";
        if (!await IsActiveUserAsync(actorId, ct)) return "forbidden";
        if (draft.PostId is { } postId)
        {
            var detail = await news.GetAsync(postId, actorId, ct);
            return detail is not null && detail.CanEdit ? null : "forbidden";
        }
        return await news.CanManageAsync(actorId, ct) ? null : "forbidden";
    }

    private Task<bool> IsActiveUserAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct);

    private async Task<bool> DraftHasVideoAsync(NewsComposerDraft draft, CancellationToken ct)
    {
        var referenced = db.NewsComposerDraftMedia.AsNoTracking().Where(r => r.DraftId == draft.Id).Select(r => r.MediaId);
        // An upload writes no ref row, so the draft scope (UploadDraftId) is what makes a just-uploaded
        // video count — otherwise a second one slips through before the first autosave.
        if (await db.NewsComposerMedia.AsNoTracking()
                .AnyAsync(m => m.Kind == KindVideo && m.DeletedAt == null
                               && (referenced.Contains(m.Id) || m.UploadDraftId == draft.Id), ct)) return true;

        if (draft.PostId is { } postId)
        {
            var promo = db.NewsPostMedia.AsNoTracking().Where(r => r.NewsPostId == postId).Select(r => r.MediaId);
            if (await db.NewsComposerMedia.AsNoTracking()
                    .AnyAsync(m => m.Kind == KindVideo && m.DeletedAt == null && promo.Contains(m.Id), ct)) return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Mapping / storage helpers
    // ──────────────────────────────────────────────────────────────────────

    private static NewsComposerMedia NewMedia(Guid draftId, Guid actorId, string kind, string contentType,
        NewsComposerMediaUpload upload, FileInfo info, string sha256) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = actorId,
        // The upload scope: this exact draft, not "any draft this user owns". It is a transient window
        // for the editor, not a permanent lock — see EligibleAsync.
        UploadDraftId = draftId,
        Kind = kind,
        FileName = SafeName(upload.FileName, Path.GetExtension(upload.FileName).ToLowerInvariant()),
        // Never the caller-supplied ContentType: it is an arbitrary client string.
        ContentType = contentType,
        Size = info.Length,
        Sha256 = sha256,
        DurationSeconds = 0,
        CreatedAt = DateTime.UtcNow,
    };

    private static NewsComposerMediaResult Ok(NewsComposerMedia media, string contextKind, Guid contextId) =>
        new(true, Dto(media, contextKind, contextId), null);

    private static NewsComposerMediaDto Dto(NewsComposerMedia m, string contextKind, Guid contextId) =>
        new(m.Id, m.Kind, m.FileName, m.ContentType, m.Size, m.DurationSeconds,
            m.ThumbnailPath is not null, contextKind, contextId);

    private static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        KindImage => KindImage,
        KindVideo => KindVideo,
        KindFile => KindFile,
        _ => null,
    };

    /// Canonical MIME for a sniffed image extension — never the client-supplied ContentType.
    private static string ImageMime(string sniffedExt) => sniffedExt switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static string VideoMime(string ext) =>
        ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    /// Canonical MIME for a regular attachment from its (already allow-listed) extension.
    private static string FileMime(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static string PosterContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static string SafeName(string fileName, string ext)
    {
        var n = Path.GetFileName(fileName).Trim();
        if (n.Length == 0) return "file" + ext;
        return n.Length <= 255 ? n : n[..(255 - ext.Length)] + ext;
    }

    private string Full(string rel) => SafeFull(rel) ?? throw new InvalidOperationException("Media path escapes the store root.");

    private string? SafeFull(string? rel)
    {
        if (rel is null) return null;
        var root = Path.GetFullPath(opt.LocalRoot);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
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

    private async Task AuditAsync(string action, Guid entityId, Guid actorId, object detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), Action = action, Entity = "NewsComposerMedia", EntityId = entityId.ToString(),
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { actor = actorId, detail }),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    /// Image magic numbers. Returns the canonical extension or null when it is not a real image.
    private static async Task<string?> SniffImageAsync(string path, CancellationToken ct)
    {
        var h = await ReadHeadAsync(path, 12, ct);
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G'
            && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return ".png";
        if (h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8'
            && (h[4] == '7' || h[4] == '9') && h[5] == 'a') return ".gif";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F'
            && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        return null;
    }

    /// MP4 has an "ftyp" box at offset 4; WebM starts with the EBML magic.
    private static async Task<bool> LooksLikeVideoAsync(string path, string ext, CancellationToken ct)
    {
        var h = await ReadHeadAsync(path, 12, ct);
        if (ext == ".webm") return h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3;
        return h.Length >= 8 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p';
    }

    private static async Task<byte[]> ReadHeadAsync(string path, int n, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var buf = new byte[n];
        var read = await fs.ReadAtLeastAsync(buf, n, throwOnEndOfStream: false, ct);
        return buf[..read];
    }

    private static NewsComposerMediaResult Fail(string code) => new(false, null, code);
}

/// <summary>Where the composer keeps video streaming copies (mirrors NewsVideoOptions, separate root).</summary>
public sealed class NewsComposerMediaOptions
{
    public required string LocalRoot { get; init; }
}