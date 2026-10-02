using System.Text.Json;
using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IPop.Infrastructure.News;

/// <summary>
/// Private, versioned News composer drafts. Every write here touches drafts only — the live
/// <see cref="NewsPost"/> is never modified, so an unfinished edit cannot reach readers. Promotion
/// (a follow-on step) copies a draft onto the live post under a compare-and-swap on both versions.
/// </summary>
public sealed partial class NewsComposerService(IPopDbContext db, INewsService news, INewsPublicationHooks? hooks = null) : INewsComposerService
{
    public const string StateActive = "active";
    public const string StateScheduled = "scheduled";
    public const string StatePromoted = "promoted";
    public const string StateDiscarded = "discarded";

    /// Body cap mirrors NewsPost.Body's column width, so autosave can never store a snapshot that
    /// promotion would then fail to write.
    public const int MaxBodyChars = 20000;
    public const int MaxTitleChars = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ──────────────────────────────────────────────────────────────────────
    // Create / resume / list
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsDraftResultDto> CreateDraftAsync(Guid ownerId, CreateNewsDraftRequest req, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(ownerId, ct)) return Fail("forbidden");

        NewsPost? post = null;
        if (req.PostId is { } postId)
        {
            // A revision draft is authorized against the live post, not against the caller's own role:
            // a revoked or inactive author must not be able to keep editing through a draft.
            var detail = await news.GetAsync(postId, ownerId, ct);
            if (detail is null) return Fail("not_found");
            if (!detail.CanEdit) return Fail("forbidden");
            post = await db.NewsPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == postId, ct);
            if (post is null) return Fail("not_found");
        }
        else if (!await news.CanManageAsync(ownerId, ct))
        {
            // New posts (no public row yet) are HR/Admin only, matching CreateAsync.
            return Fail("forbidden");
        }

        var now = DateTime.UtcNow;
        var draft = new NewsComposerDraft
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            PostId = post?.Id,
            BaseContentVersion = post?.ContentVersion,
            Version = 0,
            State = StateActive,
            BodyFormat = NewsHtml.BodyFormatPlain,
            CoverMode = "auto",
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (post is not null) await SeedFromPostAsync(draft, post, ct);

        db.NewsComposerDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return Ok(await MapAsync(draft.Id, ct));
    }

    public async Task<NewsDraftResultDto> GetDraftAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return Fail("not_found");
        // Drafts are private to their author: even HR/Admin do not read someone else's unpublished work.
        if (draft.OwnerId != actorId) return Fail("not_found");
        if (draft.State != StateActive) return Fail("not_active");
        var authErr = await AuthorizeOwnerAsync(draft, actorId, ct);
        if (authErr is not null) return Fail(authErr);
        return Ok(await MapAsync(draftId, ct));
    }

    public async Task<NewsDraftListDto> ListDraftsAsync(Guid ownerId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(ownerId, ct)) return new NewsDraftListDto([]);
        var canManage = await news.CanManageAsync(ownerId, ct);

        // Active drafts plus frozen schedules: a scheduled future must stay findable so its author can
        // review, edit-by-resume or cancel it rather than have it silently vanish.
        var drafts = await db.NewsComposerDrafts.AsNoTracking()
            .Where(d => d.OwnerId == ownerId && (d.State == StateActive || d.State == StateScheduled))
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        string ownerName = await db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";
        var items = new List<NewsDraftDto>(drafts.Count);
        foreach (var d in drafts)
        {
            // A draft the owner can no longer act on (permission revoked, post no longer editable)
            // must not be offered as a resumable item.
            if (d.PostId is { } postId)
            {
                var detail = await news.GetAsync(postId, ownerId, ct);
                if (detail is null || !detail.CanEdit) continue;
            }
            else if (!canManage) continue;

            var full = await MapAsync(d.Id, ct);
            items.Add(full);
        }
        return new NewsDraftListDto(items);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Autosave (compare-and-swap on the draft Version; never touches the live post)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsDraftResultDto> SaveDraftAsync(Guid actorId, Guid draftId, SaveNewsDraftRequest req, CancellationToken ct)
    {
        if (req.Snapshot is null) return Fail("invalid_request");

        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return Fail("not_found");
        if (draft.OwnerId != actorId) return Fail("forbidden");
        if (draft.State != StateActive) return Fail("not_active");
        var authErr = await AuthorizeOwnerAsync(draft, actorId, ct);
        if (authErr is not null) return Fail(authErr);

        var snap = req.Snapshot;
        if (snap.Body is { Length: > MaxBodyChars }) return Fail("body_too_long");
        if (snap.Title is { Length: > MaxTitleChars }) return Fail("title_too_long");

        var bodyFormat = NormalizeBodyFormat(snap.BodyFormat);
        var body = bodyFormat == NewsHtml.BodyFormatHtml ? NewsHtml.Sanitize(snap.Body) : snap.Body;

        // The markup is the source of truth for which media the body actually shows: every
        // data-news-media id in the sanitized body must resolve to a validated reference. A body
        // that smuggles in an id not backed by a reference (or vice versa) is refused rather than
        // stored, so a reader can never be served an asset the draft does not own.
        var markupIds = NewsHtml.ReferencedMediaIds(body);
        var declared = (snap.Media ?? []).Select(m => m.MediaId).ToList();
        var coverId = snap.CoverMode == "manual" ? snap.CoverMediaId : null;
        if (coverId is { } c && !declared.Contains(c)) declared.Add(c);
        if (!markupIds.All(declared.Contains)) return Fail("media_ref_mismatch");

        // Serializable + retry: the reference validation and the reference rewrite must be one
        // atomic unit against the media cleanup pass. Otherwise a save can validate a live asset,
        // the GC can tombstone it, and the save then commits a reference to a deleted asset. On
        // collision (Postgres 40001) the loser aborts and retries, and the retry sees the winner's
        // committed state. SQLite (tests) is serializable already, so this is a no-op there.
        return await WithSerializableRetryAsync(async () =>
        {
            await using var tx = await BeginWriteAsync(ct);

            // Re-read the base inside the transaction: the version predicate below is the real
            // guard, but the revision baseline must be the committed one.
            var current = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
            if (current is null) return "not_found";
            if (current.State != StateActive) return "not_active";

            var refsErr = await ValidateRefsAsync(current, actorId, declared, snap.AttachmentIds, ct);
            if (refsErr is not null) return refsErr;

            // A revision must not be saved on top of a live edit the author has not seen: the
            // snapshot would silently discard that edit on promotion. The comparison is always
            // against the draft's recorded base — a mismatched or omitted request value cannot
            // relax it.
            if (current.PostId is { } postId)
            {
                var live = await db.NewsPosts.AsNoTracking().Where(p => p.Id == postId)
                    .Select(p => (int?)p.ContentVersion).FirstOrDefaultAsync(ct);
                if (live is null) return "not_found";
                if (live.Value != current.BaseContentVersion) return "post_content_conflict";
            }

            var next = req.BaseVersion + 1;
            var now = DateTime.UtcNow;
            var title = Truncate(snap.Title?.Trim(), MaxTitleChars);
            var coverMode = NormalizeCoverMode(snap.CoverMode);
            var cover = coverMode == "manual" ? snap.CoverMediaId : null;
            var targetsJson = SerializeTargets(snap.Targets);
            var ackDue = snap.RequiresAcknowledgement ? snap.AckDueAt : null;
            var type = Truncate(snap.Type, 32);
            var pinned = snap.IsPinned;
            var requiresAck = snap.RequiresAcknowledgement;
            var publishAt = snap.PublishAt;

            // Explicit atomic predicate: only the client holding the current version may write,
            // so two tabs (or two devices) cannot silently overwrite each other.
            var won = await db.NewsComposerDrafts
                .Where(d => d.Id == draftId && d.Version == req.BaseVersion && d.State == StateActive)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Version, next)
                    .SetProperty(d => d.Type, type)
                    .SetProperty(d => d.Title, title)
                    .SetProperty(d => d.Body, body)
                    .SetProperty(d => d.BodyFormat, bodyFormat)
                    .SetProperty(d => d.RequiresAcknowledgement, requiresAck)
                    .SetProperty(d => d.AckDueAt, ackDue)
                    .SetProperty(d => d.IsPinned, pinned)
                    .SetProperty(d => d.PublishAt, publishAt)
                    .SetProperty(d => d.CoverMediaId, cover)
                    .SetProperty(d => d.CoverMode, coverMode)
                    .SetProperty(d => d.TargetsJson, targetsJson)
                    .SetProperty(d => d.UpdatedAt, now), ct);
            if (won == 0) return "version_conflict";

            await ReplaceRefsAsync(draftId, declared, snap.Media ?? [], cover, snap.AttachmentIds, ct);
            await tx.CommitAsync(ct);
            return null;
        }, draftId, ct);
    }

    /// <summary>
    /// Runs a serializable write, retrying on a serialization failure. The operation returns an
    /// error string (or null on success); a <c>version_conflict</c>/<c>post_content_conflict</c> is
    /// a real answer, not a retryable failure.
    /// </summary>
    private async Task<NewsDraftResultDto> WithSerializableRetryAsync(
        Func<Task<string?>> work, Guid draftId, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var err = await work();
                if (err is not null) return Fail(err);
                db.ChangeTracker.Clear();
                return Ok(await MapAsync(draftId, ct));
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSerializationFailure(ex))
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }

    /// <summary>PostgreSQL serialization failure (40001) surfaces as a DbUpdateException.</summary>
    private static bool IsSerializationFailure(Exception ex) =>
        ex is DbUpdateException or Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException
        || ex.InnerException is not null && IsSerializationFailure(ex.InnerException);

    public async Task<NewsDraftResultDto> ResumeScheduledDraftAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return Fail("not_found");
        if (draft.OwnerId != actorId) return Fail("forbidden");
        if (draft.State != StateScheduled) return Fail("not_active");

        var now = DateTime.UtcNow;
        var resumed = await db.NewsComposerDrafts
            .Where(d => d.Id == draftId && d.State == StateScheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.State, StateActive)
                .SetProperty(d => d.PublishAt, (DateTime?)null)   // the author re-chooses the time
                .SetProperty(d => d.NextScheduleAttemptAt, (DateTime?)null)
                .SetProperty(d => d.UpdatedAt, now)
                .SetProperty(d => d.Version, d => d.Version + 1), ct);
        if (resumed == 0) return Fail("not_active");

        db.ChangeTracker.Clear();
        return Ok(await MapAsync(draftId, ct));
    }

    public async Task<NewsDraftResultDto> DiscardDraftAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return Fail("not_found");
        // Discarding is the author's own action; it is never blocked by staleness so a confused
        // client can always get out. It also cancels a frozen schedule — the live post was never
        // touched by the freeze, so there is nothing to unwind.
        if (draft.OwnerId != actorId) return Fail("forbidden");
        if (draft.State != StateActive && draft.State != StateScheduled) return Fail("not_active");

        var now = DateTime.UtcNow;
        var won = await db.NewsComposerDrafts
            .Where(d => d.Id == draftId && (d.State == StateActive || d.State == StateScheduled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.State, StateDiscarded)
                .SetProperty(d => d.DiscardedAt, (DateTime?)now)
                .SetProperty(d => d.UpdatedAt, now)
                .SetProperty(d => d.NextScheduleAttemptAt, (DateTime?)null)
                .SetProperty(d => d.Version, d => d.Version + 1), ct);
        if (won == 0) return Fail("not_active");

        db.ChangeTracker.Clear();
        return Ok(await MapAsync(draftId, ct));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Preview
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsPreviewDto?> PreviewAsync(Guid actorId, Guid draftId, CancellationToken ct)
    {
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null || draft.OwnerId != actorId || draft.State != StateActive) return null;
        if (await AuthorizeOwnerAsync(draft, actorId, ct) is not null) return null;

        // Preview always runs the same sanitizer the reader will run, so what the author sees here is
        // what a reader gets. A plain draft keeps its literal text — escaped, line breaks preserved
        // as <br> — and is never interpreted as markup; an html draft goes through the allow-list.
        var html = draft.BodyFormat == NewsHtml.BodyFormatHtml
            ? NewsHtml.Sanitize(draft.Body)
            : NewsHtml.Sanitize(NewsHtml.RenderPlainForPreview(draft.Body));
        return new NewsPreviewDto(draft.Id, draft.Title ?? "", html,
            NewsHtml.ToPlainText(draft.Body, draft.BodyFormat), draft.BodyFormat);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes autosave mutations against this draft row. SQLite (tests) has no row locks, so the
    /// version predicate plus a single transaction is the guarantee there; PostgreSQL takes a row
    /// lock on the drafts row inside the transaction, so the CAS and the ref rewrite cannot interleave.
    /// </summary>
    private async Task<IDbContextTransaction> BeginWriteAsync(CancellationToken ct)
    {
        // Serializable on both providers: the reference validation and the reference write must not
        // interleave with the media cleanup pass, or a save can commit a reference to an asset the
        // cleanup has just tombstoned. The caller retries on a serialization failure (40001).
        return await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    }

    /// The owner must still be active and still entitled: a *new* draft needs the HR/Admin create
    /// gate on every call, and a *revision* draft is re-authorized against the live post each time.
    private async Task<string?> AuthorizeOwnerAsync(NewsComposerDraft draft, Guid actorId, CancellationToken ct)
    {
        if (!await IsActiveUserAsync(actorId, ct)) return "forbidden";
        if (draft.PostId is { } postId)
        {
            // A manager who inherited the post (e.g. the author left) edits the draft as a manager:
            // the post's own CanEdit is the gate, and manager permissions are checked per call.
            var detail = await news.GetAsync(postId, actorId, ct);
            return detail is not null && detail.CanEdit ? null : "forbidden";
        }
        return await news.CanManageAsync(actorId, ct) ? null : "forbidden";
    }

    private Task<bool> IsActiveUserAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct);

    /// <summary>
    /// Media refs must be the actor's own upload while the draft is being built; on a revision the
    /// media already referenced by the live post (or by this draft) is also allowed, so a manager can
    /// safely re-edit an existing post without re-uploading its images. Files already attached to the
    /// post are allowed the same way. Nothing outside that set can be grafted on.
    /// </summary>
    private async Task<string?> ValidateRefsAsync(NewsComposerDraft draft, Guid actorId,
        IReadOnlyList<Guid> mediaIds, IReadOnlyList<Guid>? attachmentIds, CancellationToken ct)
    {
        var media = mediaIds.Distinct().ToList();
        if (media.Count > 0)
        {
            var scoped = await ScopedMediaIdsAsync(draft, ct);
            var ok = await db.NewsComposerMedia.AsNoTracking()
                .Where(m => media.Contains(m.Id) && m.DeletedAt == null
                            && (m.OwnerId == actorId || scoped.Contains(m.Id)))
                .Select(m => m.Id).ToListAsync(ct);
            if (ok.Count != media.Count) return "invalid_media";
        }

        var attachments = (attachmentIds ?? []).Distinct().ToList();
        if (attachments.Count > 0)
        {
            var scoped = await ScopedAttachmentIdsAsync(draft, ct);
            var ok = await db.Attachments.AsNoTracking()
                .Where(a => attachments.Contains(a.Id) && a.Area == "news" && a.DeletedAt == null
                            && (a.UploaderId == actorId || scoped.Contains(a.Id)))
                .Select(a => a.Id).ToListAsync(ct);
            if (ok.Count != attachments.Count) return "invalid_attachment";
        }
        return null;
    }

    /// Media ids already referenced by the draft or by the live post it revises.
    private async Task<HashSet<Guid>> ScopedMediaIdsAsync(NewsComposerDraft draft, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(await db.NewsComposerDraftMedia.AsNoTracking()
            .Where(r => r.DraftId == draft.Id).Select(r => r.MediaId).ToListAsync(ct));
        if (draft.PostId is { } postId)
            foreach (var id in await db.NewsPostMedia.AsNoTracking()
                         .Where(r => r.NewsPostId == postId).Select(r => r.MediaId).ToListAsync(ct))
                ids.Add(id);
        return ids;
    }

    /// Attachment ids already referenced by the draft or by the live post it revises.
    private async Task<HashSet<Guid>> ScopedAttachmentIdsAsync(NewsComposerDraft draft, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(await db.NewsComposerDraftAttachments.AsNoTracking()
            .Where(r => r.DraftId == draft.Id).Select(r => r.AttachmentId).ToListAsync(ct));
        if (draft.PostId is { } postId)
            foreach (var id in await db.NewsAttachments.AsNoTracking()
                         .Where(r => r.NewsPostId == postId).Select(r => r.AttachmentId).ToListAsync(ct))
                ids.Add(id);
        return ids;
    }

    private async Task ReplaceRefsAsync(Guid draftId, IReadOnlyList<Guid> mediaIds,
        IReadOnlyList<NewsDraftMediaRefDto> mediaRefs, Guid? coverId, IReadOnlyList<Guid>? attachmentIds, CancellationToken ct)
    {
        await db.NewsComposerDraftMedia.Where(r => r.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.NewsComposerDraftAttachments.Where(r => r.DraftId == draftId).ExecuteDeleteAsync(ct);

        // Cover first, then inline; de-duplicated so one asset never gets two rows.
        var ordered = new List<(Guid Id, string Role)>();
        if (coverId is { } c) ordered.Add((c, "cover"));
        var inline = mediaRefs.Where(m => m.MediaId != coverId)
            .OrderBy(m => m.SortOrder).Select(m => m.MediaId)
            .Concat(mediaIds.Where(id => mediaRefs.All(m => m.MediaId != id)))
            .Distinct();
        foreach (var id in inline) ordered.Add((id, "inline"));

        for (var i = 0; i < ordered.Count; i++)
            db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia
            { DraftId = draftId, MediaId = ordered[i].Id, Role = ordered[i].Role, SortOrder = i });

        var attachments = (attachmentIds ?? []).Distinct().ToList();
        for (var i = 0; i < attachments.Count; i++)
            db.NewsComposerDraftAttachments.Add(new NewsComposerDraftAttachment
            { DraftId = draftId, AttachmentId = attachments[i], SortOrder = i });

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedFromPostAsync(NewsComposerDraft draft, NewsPost post, CancellationToken ct)
    {
        draft.Type = post.Type;
        draft.Title = post.Title;
        draft.Body = post.Body;
        draft.BodyFormat = NormalizeBodyFormat(post.BodyFormat);
        draft.RequiresAcknowledgement = post.RequiresAcknowledgement;
        draft.AckDueAt = post.AckDueAt;
        draft.IsPinned = post.IsPinned;
        draft.PublishAt = post.PublishAt;
        draft.CoverMediaId = post.CoverMediaId;
        draft.CoverMode = NormalizeCoverMode(post.CoverMode);
        draft.TargetsJson = SerializeTargets(await db.NewsTargets.AsNoTracking()
            .Where(t => t.NewsPostId == post.Id)
            .Select(t => new NewsTargetInput(t.Kind, t.Value)).ToListAsync(ct));

        foreach (var a in await db.NewsAttachments.AsNoTracking()
                     .Where(a => a.NewsPostId == post.Id && a.Attachment.DeletedAt == null)
                     .OrderBy(a => a.SortOrder).Select(a => a.AttachmentId).ToListAsync(ct))
            db.NewsComposerDraftAttachments.Add(new NewsComposerDraftAttachment
            { DraftId = draft.Id, AttachmentId = a, SortOrder = 0 });

        foreach (var m in await db.NewsPostMedia.AsNoTracking()
                     .Where(m => m.NewsPostId == post.Id)
                     .Select(m => new { m.MediaId, m.Role, m.SortOrder }).ToListAsync(ct))
            db.NewsComposerDraftMedia.Add(new NewsComposerDraftMedia
            { DraftId = draft.Id, MediaId = m.MediaId, Role = NormalizeRole(m.Role), SortOrder = m.SortOrder });
    }

    private async Task<NewsDraftDto> MapAsync(Guid draftId, CancellationToken ct)
    {
        var d = await db.NewsComposerDrafts.AsNoTracking().FirstAsync(x => x.Id == draftId, ct);
        var ownerName = await db.Users.AsNoTracking().Where(u => u.Id == d.OwnerId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";
        var postTitle = d.PostId is { } pid
            ? await db.NewsPosts.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Title).FirstOrDefaultAsync(ct) ?? ""
            : "";

        var media = await db.NewsComposerDraftMedia.AsNoTracking().Where(r => r.DraftId == draftId)
            .OrderBy(r => r.SortOrder).Select(r => new NewsDraftMediaRefDto(r.MediaId, r.Role, r.SortOrder)).ToListAsync(ct);
        var attachments = await db.NewsComposerDraftAttachments.AsNoTracking().Where(r => r.DraftId == draftId)
            .OrderBy(r => r.SortOrder).Select(r => r.AttachmentId).ToListAsync(ct);

        var snapshot = new NewsDraftSnapshotDto(
            d.Type, d.Title, d.Body, d.BodyFormat, d.RequiresAcknowledgement, d.AckDueAt, d.IsPinned,
            d.PublishAt, d.CoverMediaId, d.CoverMode, DeserializeTargets(d), attachments, media);

        return new NewsDraftDto(d.Id, d.OwnerId, ownerName, d.PostId, postTitle,
            d.BaseContentVersion, d.Version, d.State, snapshot, d.CreatedAt, d.UpdatedAt);
    }

    private static string NormalizeBodyFormat(string? format) =>
        string.Equals(format, NewsHtml.BodyFormatHtml, StringComparison.OrdinalIgnoreCase)
            ? NewsHtml.BodyFormatHtml : NewsHtml.BodyFormatPlain;

    private static string NormalizeCoverMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "manual" => "manual",
        "none" => "none",
        _ => "auto",
    };

    private static string NormalizeRole(string? role) =>
        string.Equals(role, "cover", StringComparison.OrdinalIgnoreCase) ? "cover" : "inline";

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static string? SerializeTargets(IReadOnlyList<NewsTargetInput>? targets) =>
        targets is null || targets.Count == 0 ? null : JsonSerializer.Serialize(targets, Json);

    private static IReadOnlyList<NewsTargetInput>? DeserializeTargets(NewsComposerDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.TargetsJson)) return null;
        try { return JsonSerializer.Deserialize<List<NewsTargetInput>>(d.TargetsJson, Json); }
        catch (JsonException) { return null; }
    }

    private static NewsDraftResultDto Fail(string error) => new(false, null, error);
    private static NewsDraftResultDto Ok(NewsDraftDto draft) => new(true, draft, null);
}