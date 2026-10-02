using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IPop.Infrastructure.News;

/// <summary>
/// Promotion: copy a private draft snapshot onto the live post. The live post is untouched until
/// promotion succeeds — that is the whole point of the draft layer.
///
/// <para><b>Publish</b> writes now under a compare-and-swap on the draft Version (and, for a
/// revision, on the live ContentVersion), so a stale tab can never overwrite a newer live edit and
/// a retry can never publish twice. <b>Schedule</b> does NOT write: it freezes the draft as
/// "scheduled" and the worker applies it when due, so a scheduled revision never leaks its content
/// before its time and never hides the post that is still live.</para>
/// </summary>
public sealed partial class NewsComposerService
{
    public Task<NewsPostResultDto> PromoteDraftAsync(Guid actorId, Guid draftId, PromoteNewsDraftRequest req, CancellationToken ct)
        => PromoteCoreAsync(actorId, draftId, req, expectedState: StateActive, ct);

    /// <summary>
    /// Promotion core. <paramref name="expectedState"/> is the state the caller may promote from:
    /// "active" for an author publishing now, "scheduled" for the worker applying a due freeze.
    /// </summary>
    private async Task<NewsPostResultDto> PromoteCoreAsync(Guid actorId, Guid draftId, PromoteNewsDraftRequest req,
        string expectedState, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == actorId && u.IsActive, ct))
            return PostFail("forbidden");

        var mode = NormalizeMode(req.Mode);
        if (mode is null) return PostFail("invalid_request");

        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return PostFail("not_found");
        if (draft.OwnerId != actorId) return PostFail("forbidden");
        if (draft.State != expectedState) return PostFail("not_active");

        // Publishing is HR/Admin only, exactly as the legacy direct-publish path. An author without
        // news.publish cannot bypass the gate by holding a draft.
        if (!await news.CanManageAsync(actorId, ct)) return PostFail("forbidden");

        // A revision must be promoted on top of the live version it was forked from. A draft that
        // already owns a post (a scheduled one being applied) carries its baseline the same way.
        if (draft.PostId is { } revisionPostId)
        {
            var liveVersion = await db.NewsPosts.AsNoTracking().Where(p => p.Id == revisionPostId)
                .Select(p => (int?)p.ContentVersion).FirstOrDefaultAsync(ct);
            if (liveVersion is null) return PostFail("not_found");
            if (draft.BaseContentVersion is { } baseline && liveVersion.Value != baseline)
                return PostFail("post_content_conflict");
        }

        var targets = DeserializeTargets(draft);
        var validate = ValidateForPromotion(draft, mode, targets);
        if (validate is not null) return PostFail(validate);

        // Reference validation is deliberately NOT done here: it must happen inside the write
        // transaction (see ApplyCoreAsync/FreezeAsync), otherwise the media cleanup can tombstone an
        // asset between the check and the write.
        if (mode == "schedule") return await FreezeAsync(draftId, req, draft, ct);
        // "draft" only makes sense for a post that does not exist yet: applying it to a live post
        // would unpublish content readers are already seeing, so it is refused instead.
        if (mode == "draft" && draft.PostId is not null) return PostFail("invalid_request");
        return await ApplyCoreAsync(actorId, draftId, req, draft, targets, mode, ct);
    }

    /// <summary>
    /// Freeze a draft as "scheduled". Nothing is written to the live post: the content, the audience
    /// and the publish time stay in the private draft until the worker applies them. Returns the
    /// post currently live for a revision (unchanged), or null for a post that does not exist yet.
    /// </summary>
    private async Task<NewsPostResultDto> FreezeAsync(Guid draftId, PromoteNewsDraftRequest req,
        NewsComposerDraft draft, CancellationToken ct)
    {
        // The freeze does not need the draft's references, but it does need the time it will fire at.
        var publishAt = draft.PublishAt;
        if (publishAt is null || publishAt <= DateTime.UtcNow) return PostFail("schedule_requires_future_time");

        await using var tx = await BeginWriteAsync(ct);
        // References are re-validated inside the transaction so the frozen snapshot cannot point at
        // an asset the media cleanup removed between the pre-flight read and this write.
        var refsErr = await ValidatePromotionRefsAsync(draft, draft.OwnerId, ct);
        if (refsErr is not null) return PostFail(refsErr);

        var now = DateTime.UtcNow;
        var claimed = await db.NewsComposerDrafts
            .Where(d => d.Id == draftId && d.Version == req.BaseVersion && d.State == StateActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.State, StateScheduled)
                .SetProperty(d => d.PublishAt, publishAt)
                // The reset choice has to survive the freeze: the worker has no request to read.
                .SetProperty(d => d.ResetAcknowledgementsOnPromote, req.ResetAcknowledgements)
                .SetProperty(d => d.UpdatedAt, now)
                .SetProperty(d => d.Version, d => d.Version + 1), ct);
        if (claimed == 0) return PostFail("version_conflict");
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        var live = draft.PostId is { } postId ? await news.GetAsync(postId, draft.OwnerId, ct) : null;
        return new NewsPostResultDto(true, live, null);
    }

    /// <summary>
    /// The actual write: claim the draft, write the post, move the references, optionally reset
    /// acknowledgements, then commit and fire the after-commit hook exactly once.
    /// </summary>
    private async Task<NewsPostResultDto> ApplyCoreAsync(Guid actorId, Guid draftId, PromoteNewsDraftRequest req,
        NewsComposerDraft draft, IReadOnlyList<NewsTargetInput>? targets, string mode, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(actorId, draftId, req, draft, targets, mode, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSerializationFailure(ex))
            {
                // A concurrent write won the serializable race: retry, and the re-read sees its
                // committed state (the claim CAS will then report version_conflict if it beat us).
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }

    private async Task<NewsPostResultDto> ApplyOnceAsync(Guid actorId, Guid draftId, PromoteNewsDraftRequest req,
        NewsComposerDraft draft, IReadOnlyList<NewsTargetInput>? targets, string mode, CancellationToken ct)
    {
        var publish = mode == "publish";
        await using var tx = await BeginWriteAsync(ct);
        var now = DateTime.UtcNow;

        // Claim the draft first: the CAS is what makes promotion idempotent under a retry or a
        // concurrent double-click. Only one caller can move it out of the expected state.
        var claimed = await db.NewsComposerDrafts
            .Where(d => d.Id == draftId && d.Version == req.BaseVersion && d.State == draft.State)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.State, StatePromoted)
                .SetProperty(d => d.PromotedAt, now)
                .SetProperty(d => d.UpdatedAt, now)
                .SetProperty(d => d.Version, d => d.Version + 1), ct);
        if (claimed == 0) return PostFail("version_conflict");
        db.ChangeTracker.Clear();

        // Reference validation inside the transaction (not before it): the media cleanup pass must
        // not be able to tombstone a referenced asset between the check and the write below.
        var refsErr = await ValidatePromotionRefsAsync(draft, actorId, ct);
        if (refsErr is not null) return PostFail(refsErr);

        // Resolve the cover once, before any write, so the post and the reference rows agree.
        //   manual -> the chosen image (validated to be an image by ValidatePromotionRefsAsync)
        //   auto   -> the first validated inline IMAGE in body order (never a video/file)
        //   none   -> no cover, deliberately, so nothing can re-derive one later
        var resolvedCover = draft.CoverMode switch
        {
            "manual" => draft.CoverMediaId,
            "auto" => await FirstBodyImageAsync(draft, ct),
            _ => null,
        };

        var isNew = draft.PostId is null;
        NewsPost post;
        if (isNew)
        {
            post = new NewsPost
            {
                Id = Guid.NewGuid(),
                Type = draft.Type!,
                Title = draft.Title!.Trim(),
                Body = draft.Body ?? "",
                BodyFormat = draft.BodyFormat,
                Status = publish ? "published" : "draft",
                AuthorId = actorId,
                ApproverId = publish ? actorId : null,
                RequiresAcknowledgement = draft.RequiresAcknowledgement,
                AckDueAt = draft.RequiresAcknowledgement ? draft.AckDueAt : null,
                IsPinned = draft.IsPinned,
                PinnedAt = draft.IsPinned ? now : null,
                CoverMediaId = resolvedCover,
                CoverMode = draft.CoverMode,
                CreatedAt = now,
                UpdatedAt = now,
                PublishedAt = publish ? now : null,
            };
            db.NewsPosts.Add(post);
            await db.SaveChangesAsync(ct);
            // A free-standing draft adopts the post it created (so the worker and later revisions
            // can address it); a draft created from a post already has its id.
            await db.NewsComposerDrafts.Where(d => d.Id == draftId && d.PostId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.PostId, (Guid?)post.Id), ct);
        }
        else
        {
            var postId = draft.PostId!.Value;
            var baseline = draft.BaseContentVersion ?? 0;
            // Atomic guard against a live edit landing between the pre-flight check and this write:
            // the update only applies while the post is still at the version the draft forked from.
            // On PostgreSQL this also takes the row lock for the rest of the transaction, so a
            // concurrent writer cannot slip in behind it.
            var stillCurrent = await db.NewsPosts
                .Where(p => p.Id == postId && p.ContentVersion == baseline)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ContentVersion, baseline), ct);
            if (stillCurrent == 0) return PostFail("post_content_conflict");
            db.ChangeTracker.Clear();

            post = await db.NewsPosts.FirstAsync(p => p.Id == postId, ct);
            var wasLive = post.Status == "published";
            post.Type = draft.Type!;
            post.Title = draft.Title!.Trim();
            post.Body = draft.Body ?? "";
            post.BodyFormat = draft.BodyFormat;
            post.RequiresAcknowledgement = draft.RequiresAcknowledgement;
            post.AckDueAt = draft.RequiresAcknowledgement ? draft.AckDueAt : null;
            post.CoverMediaId = resolvedCover;
            post.CoverMode = draft.CoverMode;
            post.IsPinned = draft.IsPinned;
            post.PinnedAt = draft.IsPinned ? (post.PinnedAt ?? now) : null;
            if (publish)
            {
                post.Status = "published";
                post.ApproverId = actorId;
                post.RejectionReason = null;
                post.PublishedAt ??= now;
                post.PublishAt = null;   // publishing now; the freeze was the scheduling step
            }
            post.UpdatedAt = now;
            post.ContentVersion++;
            if (wasLive && publish) post.EditedAt = now;
            await db.SaveChangesAsync(ct);
        }

        await ReplaceTargetsAsync(post.Id, targets, ct);
        await ReplacePostAttachmentsAsync(post.Id, draftId, ct);
        await ReplacePostMediaAsync(post.Id, draftId, ct);

        db.NewsAudits.Add(new NewsAudit
        {
            Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId,
            Action = isNew ? "created" : "edited",
            Detail = "promoted", CreatedAt = now,
        });
        if (publish)
            db.NewsAudits.Add(new NewsAudit
            {
                Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId,
                Action = "published", CreatedAt = now
            });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), Action = "news.composer.promote", Entity = "NewsPost",
            EntityId = post.Id.ToString(),
            DetailJson = $"{{\"actor\":\"{actorId}\",\"draft\":\"{draftId}\",\"mode\":\"{mode}\"}}",
            CreatedAt = now
        });

        // Acknowledgement reset is opt-in and on a publish of a post that already existed. The reset only
        // matters when the audience must confirm the NEW content, so the hook is told to notify for a
        // first publish or a reset, and stays quiet for an ordinary content edit.
        var resetAcks = publish && req.ResetAcknowledgements && !isNew;
        if (resetAcks)
        {
            await db.NewsAcknowledgements.Where(a => a.NewsPostId == post.Id).ExecuteDeleteAsync(ct);
            db.NewsAudits.Add(new NewsAudit
            {
                Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId,
                Action = "acknowledgements_reset", CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        // After-commit side effects. The hook is not idempotent, so it runs strictly once, only after
        // the promotion committed; the draft-state CAS above is what prevents a second firing.
        // Notify only when the audience actually needs telling: a brand-new publication, or a reset
        // that invalidates existing confirmations. A plain content update just refreshes the feed.
        if (hooks is not null && publish)
        {
            try { await hooks.AfterPublishedAsync(post.Id, notify: isNew || resetAcks, ct); }
            catch { /* delivery is best-effort; the promotion itself already committed */ }
        }

        return new NewsPostResultDto(true, await news.GetAsync(post.Id, actorId, ct), null);
    }

    /// <summary>
    /// Apply a frozen scheduled draft that is now due. Re-checks the promoter's permission — the
    /// original author may have left or lost the right — and only then publishes. Safe to call
    /// repeatedly: the draft state is the guard.
    /// </summary>
    public async Task<NewsDraftResultDto?> ApplyScheduledDraftAsync(Guid draftId, CancellationToken ct)
    {
        var draft = await db.NewsComposerDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null || draft.State != StateScheduled) return null;
        if (draft.PublishAt is null || draft.PublishAt > DateTime.UtcNow) return null;

        // The worker has no caller identity, so permission is re-checked against the draft's owner:
        // if that account is gone or no longer entitled, the draft is left frozen for a human.
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == draft.OwnerId && u.IsActive, ct)) return null;
        if (!await news.CanManageAsync(draft.OwnerId, ct)) return null;

        var result = await PromoteCoreAsync(draft.OwnerId, draftId,
            new PromoteNewsDraftRequest(draft.Version, ResetAcknowledgements: draft.ResetAcknowledgementsOnPromote),
            StateScheduled, ct);
        return result.Success
            ? new NewsDraftResultDto(true, await MapAsync(draftId, ct), null)
            : new NewsDraftResultDto(false, null, result.Error);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Promotion helpers
    // ──────────────────────────────────────────────────────────────────────

    private static string? NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "publish" => "publish",
        "schedule" => "schedule",
        "draft" => "draft",
        _ => null,
    };

    /// <summary>Valid post types, mirroring NewsService so promotion cannot mint a bogus type.</summary>
    private static readonly HashSet<string> ValidTypes = ["news", "announcement", "memo", "condolence", "duka"];

    /// <summary>Regular attachments per post, mirroring the upload policy.</summary>
    public const int MaxAttachments = 10;

    /// Promotion is the only place incomplete content is rejected: the draft itself accepts anything.
    /// Content errors are reported before type/format ones, which is the more useful order for an
    /// author staring at a publish button.
    private static string? ValidateForPromotion(NewsComposerDraft draft, string mode, IReadOnlyList<NewsTargetInput>? targets)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) return "title_required";
        if (draft.Title.Trim().Length > MaxTitleChars) return "title_too_long";
        if (string.IsNullOrWhiteSpace(draft.Body)) return "body_required";
        if (draft.Body.Length > MaxBodyChars) return "body_too_long";
        if (draft.Type is null || !ValidTypes.Contains(draft.Type)) return "invalid_type";
        if (draft.BodyFormat is not (NewsHtml.BodyFormatPlain or NewsHtml.BodyFormatHtml)) return "invalid_body_format";
        if (mode == "schedule" && (draft.PublishAt is null || draft.PublishAt <= DateTime.UtcNow))
            return "schedule_requires_future_time";
        if (targets is not null)
        {
            foreach (var t in targets)
            {
                var kind = t.Kind?.Trim().ToLowerInvariant();
                if (kind is not ("all" or "department" or "role" or "user" or "project")) return "invalid_target_kind";
                if (kind != "all" && string.IsNullOrWhiteSpace(t.Value)) return "invalid_target_value";
                if (kind == "project" && !Guid.TryParse(t.Value, out _)) return "invalid_target_value";
            }
        }
        return null;
    }

    /// Media/attachment references must still be resolvable and still belong to the draft context.
    private async Task<string?> ValidatePromotionRefsAsync(NewsComposerDraft draft, Guid actorId, CancellationToken ct)
    {
        var mediaIds = await db.NewsComposerDraftMedia.AsNoTracking()
            .Where(r => r.DraftId == draft.Id).Select(r => r.MediaId)
            .Concat(draft.CoverMediaId is { } cover ? new[] { cover } : [])
            .Distinct().ToListAsync(ct);
        if (mediaIds.Count > 0)
        {
            var scoped = await ScopedMediaIdsAsync(draft, ct);
            var ok = await db.NewsComposerMedia.AsNoTracking()
                .Where(m => mediaIds.Contains(m.Id) && m.DeletedAt == null
                            && (m.OwnerId == actorId || scoped.Contains(m.Id)))
                .Select(m => m.Id).ToListAsync(ct);
            if (ok.Count != mediaIds.Count) return "invalid_media";
        }

        // A "manual" cover must name an image the draft actually references, and it must be an image:
        // a video can never be the feed cover, and a missing id is an error rather than a silent
        // fallback to auto (which would surprise the author who chose one). Checked independently of
        // the media count, because a manual cover with no id has no media ids to inspect.
        if (draft.CoverMode == "manual")
        {
            if (draft.CoverMediaId is not { } coverId) return "cover_required";
            var kind = await db.NewsComposerMedia.AsNoTracking()
                .Where(m => m.Id == coverId && m.DeletedAt == null).Select(m => m.Kind).FirstOrDefaultAsync(ct);
            if (kind is null) return "invalid_media";
            if (kind != "image") return "invalid_cover";
            if (!mediaIds.Contains(coverId)) return "media_ref_mismatch";
        }

        var attachmentIds = await db.NewsComposerDraftAttachments.AsNoTracking()
            .Where(r => r.DraftId == draft.Id).Select(r => r.AttachmentId).ToListAsync(ct);
        if (attachmentIds.Count > MaxAttachments) return "too_many_attachments";
        if (attachmentIds.Count > 0)
        {
            var scoped = await ScopedAttachmentIdsAsync(draft, ct);
            var ok = await db.Attachments.AsNoTracking()
                .Where(a => attachmentIds.Contains(a.Id) && a.Area == "news" && a.DeletedAt == null
                            && (a.UploaderId == actorId || scoped.Contains(a.Id)))
                .Select(a => a.Id).ToListAsync(ct);
            if (ok.Count != attachmentIds.Count) return "invalid_attachment";
        }
        return null;
    }

    /// <summary>
    /// First inline image in body order, for an "auto" cover. The body is the source of truth for
    /// order (that is what the author sees), and only image-kind assets qualify — a video or a
    /// regular file must never become the feed cover. Returns null when the body has no image.
    /// </summary>
    private async Task<Guid?> FirstBodyImageAsync(NewsComposerDraft draft, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(draft.Body)) return null;
        var ordered = NewsHtml.ReferencedMediaIds(draft.Body);
        if (ordered.Count == 0) return null;
        var images = await db.NewsComposerMedia.AsNoTracking()
            .Where(m => ordered.Contains(m.Id) && m.Kind == "image" && m.DeletedAt == null)
            .Select(m => m.Id).ToListAsync(ct);
        if (images.Count == 0) return null;
        foreach (var id in ordered)
            if (images.Contains(id)) return id;
        return null;
    }

    private async Task ReplaceTargetsAsync(Guid postId, IReadOnlyList<NewsTargetInput>? targets, CancellationToken ct)
    {
        await db.NewsTargets.Where(t => t.NewsPostId == postId).ExecuteDeleteAsync(ct);
        if (targets is null || targets.Count == 0) return;
        foreach (var t in targets)
        {
            var kind = t.Kind!.Trim().ToLowerInvariant();
            var value = t.Value?.Trim();
            if (kind == "all") value = null;
            if (kind == "project" && value is not null && Guid.TryParse(value, out var g)) value = g.ToString("D");
            db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = postId, Kind = kind, Value = value });
        }
    }

    /// Replace the post's attachments with the draft's. Files detached by the promotion are NOT
    /// soft-deleted here — the cleanup pass owns file lifetime, and a discarded draft or a rollback
    /// may still reference them.
    private async Task ReplacePostAttachmentsAsync(Guid postId, Guid draftId, CancellationToken ct)
    {
        var wanted = await db.NewsComposerDraftAttachments.AsNoTracking()
            .Where(r => r.DraftId == draftId).OrderBy(r => r.SortOrder)
            .Select(r => r.AttachmentId).ToListAsync(ct);

        await db.NewsAttachments.Where(a => a.NewsPostId == postId).ExecuteDeleteAsync(ct);
        for (var i = 0; i < wanted.Count; i++)
            db.NewsAttachments.Add(new NewsAttachment { NewsPostId = postId, AttachmentId = wanted[i], SortOrder = i });
        await db.SaveChangesAsync(ct);
    }

    /// Replace the post's composer media references with the draft's, preserving role and order.
    private async Task ReplacePostMediaAsync(Guid postId, Guid draftId, CancellationToken ct)
    {
        var refs = await db.NewsComposerDraftMedia.AsNoTracking()
            .Where(r => r.DraftId == draftId).OrderBy(r => r.SortOrder)
            .Select(r => new { r.MediaId, r.Role, r.SortOrder }).ToListAsync(ct);

        await db.NewsPostMedia.Where(m => m.NewsPostId == postId).ExecuteDeleteAsync(ct);
        foreach (var r in refs)
            db.NewsPostMedia.Add(new NewsPostMedia
            { NewsPostId = postId, MediaId = r.MediaId, Role = r.Role, SortOrder = r.SortOrder });
        await db.SaveChangesAsync(ct);
    }

    private static NewsPostResultDto PostFail(string error) => new(false, null, error);
}