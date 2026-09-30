using IPop.Application.News;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.News;

using IPop.Application.Rbac;

public sealed class NewsService(IPopDbContext db, INotificationGatewayService? notificationGateway = null, IPermissionEvaluator? permEval = null, IRealtime? realtime = null) : INewsService
{
    private static readonly HashSet<string> ValidTypes = ["news", "announcement", "memo", "condolence", "duka"];
    private static readonly HashSet<string> ValidStatuses = ["draft", "pending", "published", "rejected"];
    private static readonly HashSet<string> ValidTargetKinds = ["all", "department", "role", "user", "project"];

    // ──────────────────────────────────────────────────────────────────────
    // Create
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> CreateAsync(Guid authorId, CreateNewsRequest req, CancellationToken ct)
    {
        if (!await CanManageAsync(authorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var err = ValidateCreate(req);
        if (err is not null) return new NewsResultDto(false, null, err);

        var targetsErr = ValidateTargets(req.Targets);
        if (targetsErr is not null) return new NewsResultDto(false, null, targetsErr);

        var now = DateTime.UtcNow;
        var post = new NewsPost
        {
            Id = Guid.NewGuid(),
            Type = req.Type.ToLowerInvariant().Trim(),
            Title = req.Title.Trim(),
            Body = req.Body,
            Status = "draft",
            AuthorId = authorId,
            PublishAt = req.PublishAt,
            RequiresAcknowledgement = req.RequiresAcknowledgement,
            AckDueAt = req.RequiresAcknowledgement ? req.AckDueAt : null,
            IsPinned = req.IsPinned,
            PinnedAt = req.IsPinned ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.NewsPosts.Add(post);
        await ReplaceTargetsAsync(post.Id, req.Targets, ct);

        db.NewsAudits.Add(new NewsAudit
        {
            Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = authorId,
            Action = "created", Detail = post.Type, CreatedAt = now
        });
        if (post.IsPinned)
            db.NewsAudits.Add(new NewsAudit
            {
                Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = authorId,
                Action = "pinned", CreatedAt = now
            });

        AuditLog(post.Id, "news.create", authorId, now);
        await ReplaceAttachmentsAsync(post.Id, req.AttachmentIds, authorId, ct);
        await db.SaveChangesAsync(ct);

        // "publish" goes live now; "schedule" publishes with a future PublishAt (hidden until then).
        var mode = req.Mode?.Trim().ToLowerInvariant();
        if (mode == "schedule" && (post.PublishAt is null || post.PublishAt <= now))
            return new NewsResultDto(false, await MapDetailAsync(post.Id, authorId, ct), "schedule_requires_future_time");
        if (mode is "publish" or "schedule")
        {
            if (mode == "publish" && post.PublishAt is not null)
            {
                post.PublishAt = null;
                await db.SaveChangesAsync(ct);
            }
            return await PublishAsync(authorId, post.Id, ct);
        }
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, authorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Update: the author edits their own post (any status); HR/Admin edit any post.
    // Pinning and rescheduling a live post stay HR/Admin-only.
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> UpdateAsync(Guid actorId, Guid newsId, UpdateNewsRequest req, CancellationToken ct)
    {
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        var canManage = await CanManageAsync(actorId, ct);
        if (!canManage && post.AuthorId != actorId)
            return new NewsResultDto(false, null, "forbidden");
        // Authors edit content, audience and attachments; pin state and a live post's schedule are
        // HR/Admin controls (the editor always sends them, so drop rather than reject them).
        if (!canManage)
            req = post.Status == "published"
                ? req with { IsPinned = null, PublishAt = null, ClearPublishAt = null }
                : req with { IsPinned = null };

        if (req.Type is not null && !ValidTypes.Contains(req.Type.ToLowerInvariant().Trim()))
            return new NewsResultDto(false, null, "invalid_type");
        if (req.Title is not null && string.IsNullOrWhiteSpace(req.Title))
            return new NewsResultDto(false, null, "title_required");
        if (req.Title is not null && req.Title.Trim().Length > 300)
            return new NewsResultDto(false, null, "title_too_long");
        if (req.Body is not null && req.Body.Trim().Length == 0)
            return new NewsResultDto(false, null, "body_required");
        if (req.Targets is not null)
        {
            var tErr = ValidateTargets(req.Targets);
            if (tErr is not null) return new NewsResultDto(false, null, tErr);
        }

        var changed = false;
        var contentChanged = false;
        if (req.Title is not null && req.Title.Trim() != post.Title) { post.Title = req.Title.Trim(); changed = contentChanged = true; }
        if (req.Body is not null && req.Body != post.Body) { post.Body = req.Body; changed = contentChanged = true; }
        if (req.Type is not null && req.Type.ToLowerInvariant().Trim() != post.Type) { post.Type = req.Type.ToLowerInvariant().Trim(); changed = contentChanged = true; }
        if (req.ClearPublishAt == true && post.PublishAt is not null) { post.PublishAt = null; changed = true; }
        else if (req.PublishAt is not null && req.PublishAt != post.PublishAt) { post.PublishAt = req.PublishAt; changed = true; }
        if (req.RequiresAcknowledgement is not null && req.RequiresAcknowledgement.Value != post.RequiresAcknowledgement)
        {
            post.RequiresAcknowledgement = req.RequiresAcknowledgement.Value;
            if (!post.RequiresAcknowledgement) post.AckDueAt = null;
            changed = true;
        }
        if (req.ClearAckDueAt == true && post.AckDueAt is not null) { post.AckDueAt = null; changed = true; }
        else if (req.AckDueAt is not null && post.RequiresAcknowledgement && req.AckDueAt != post.AckDueAt) { post.AckDueAt = req.AckDueAt; changed = true; }
        if (req.AttachmentIds is not null && await ReplaceAttachmentsAsync(post.Id, req.AttachmentIds, actorId, ct)) changed = true;
        if (req.IsPinned is not null && req.IsPinned.Value != post.IsPinned)
        {
            post.IsPinned = req.IsPinned.Value;
            post.PinnedAt = post.IsPinned ? DateTime.UtcNow : null;
            changed = true;
        }
        if (req.Targets is not null)
        {
            await ReplaceTargetsAsync(post.Id, req.Targets, ct);
            changed = true;
        }

        if (changed)
        {
            post.UpdatedAt = DateTime.UtcNow;
            // Legacy rejected -> draft on any edit. Published posts stay live; readers see "Edited"
            // and existing acknowledgements remain valid.
            if (post.Status == "rejected") post.Status = "draft";
            var livePost = post.Status == "published";
            if (livePost && contentChanged) post.EditedAt = post.UpdatedAt;
            db.NewsAudits.Add(new NewsAudit
            {
                Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId,
                Action = "edited", Detail = livePost ? "after_publish" : null, CreatedAt = post.UpdatedAt
            });
            AuditLog(post.Id, "news.edit", actorId, post.UpdatedAt);
            await db.SaveChangesAsync(ct);
            await BroadcastChangedAsync(ct);
        }

        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Workflow: draft -> pending -> published | rejected
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> SubmitAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.AuthorId != actorId && !await IsAdminAsync(actorId, ct))
            return new NewsResultDto(false, null, "forbidden");
        if (post.Status != "draft") return new NewsResultDto(false, null, "invalid_transition");

        post.Status = "pending";
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "submitted", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.submit", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> ApproveAsync(Guid approverId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(approverId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "pending") return new NewsResultDto(false, null, "invalid_transition");

        post.Status = "published";
        post.ApproverId = approverId;
        post.RejectionReason = null;
        post.PublishedAt = DateTime.UtcNow;
        post.UpdatedAt = post.PublishedAt.Value;
        // If scheduled publishAt is in the past, keep it; otherwise the read filter will hide until then.
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = approverId, Action = "approved", CreatedAt = post.UpdatedAt });
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = approverId, Action = "published", Detail = post.PublishAt?.ToString("O"), CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.approve", approverId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        await NotifyPublishedAsync(post, ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, approverId, ct), null);
    }

    public async Task<NewsResultDto> RejectAsync(Guid approverId, Guid newsId, string reason, CancellationToken ct)
    {
        if (!await CanManageAsync(approverId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "pending") return new NewsResultDto(false, null, "invalid_transition");
        if (string.IsNullOrWhiteSpace(reason)) return new NewsResultDto(false, null, "rejection_reason_required");

        post.Status = "rejected";
        post.ApproverId = approverId;
        post.RejectionReason = reason.Trim();
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = approverId, Action = "rejected", Detail = post.RejectionReason, CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.reject", approverId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, approverId, ct), null);
    }

    /// Direct publish by HR/Admin from draft (or legacy pending/rejected). A future PublishAt
    /// makes this a scheduled post: published but hidden from readers until that time.
    public async Task<NewsResultDto> PublishAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status == "published") return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);

        post.Status = "published";
        post.ApproverId = actorId;
        post.RejectionReason = null;
        post.PublishedAt = DateTime.UtcNow;
        post.UpdatedAt = post.PublishedAt.Value;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "published", Detail = post.PublishAt?.ToString("O"), CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.publish", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        await NotifyPublishedAsync(post, ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> UnpublishAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "published") return new NewsResultDto(false, null, "invalid_transition");

        post.Status = "draft";
        post.PublishedAt = null;
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "unpublished", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.unpublish", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Pin
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> PinAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        post.IsPinned = true;
        post.PinnedAt = DateTime.UtcNow;
        post.UpdatedAt = post.PinnedAt.Value;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "pinned", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.pin", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> UnpinAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        post.IsPinned = false;
        post.PinnedAt = null;
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "unpinned", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.unpin", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Acknowledgement (mandatory read)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> AcknowledgeAsync(Guid userId, Guid newsId, CancellationToken ct)
    {
        var post = await db.NewsPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "published") return new NewsResultDto(false, null, "not_published");
        if (post.PublishAt is not null && post.PublishAt > DateTime.UtcNow) return new NewsResultDto(false, null, "not_published");
        if (!await IsVisibleToAsync(newsId, userId, ct)) return new NewsResultDto(false, null, "forbidden");

        var exists = await db.NewsAcknowledgements.AnyAsync(x => x.NewsPostId == newsId && x.UserId == userId, ct);
        if (!exists)
        {
            var now = DateTime.UtcNow;
            db.NewsAcknowledgements.Add(new NewsAcknowledgement { NewsPostId = newsId, UserId = userId, AcknowledgedAt = now });
            db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = newsId, ActorId = userId, Action = "acknowledged", CreatedAt = now });
            AuditLog(newsId, "news.acknowledge", userId, now);
            await db.SaveChangesAsync(ct);
            await NotifyUserChangedAsync(userId, ct);
        }
        return new NewsResultDto(true, await MapDetailAsync(newsId, userId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Read
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsDetailDto?> GetAsync(Guid newsId, Guid viewerId, CancellationToken ct)
    {
        var exists = await db.NewsPosts.AsNoTracking().AnyAsync(x => x.Id == newsId, ct);
        if (!exists) return null;

        // Visibility gate
        if (!await CanManageAsync(viewerId, ct))
        {
            // Reader must be in targeting + post must be published and not future-scheduled
            var post = await db.NewsPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == newsId, ct);
            if (post is null) return null;
            // Authors always see their own post, whatever its status or audience.
            if (post.AuthorId == viewerId) return await MapDetailAsync(newsId, viewerId, ct);
            if (post.Status != "published") return null;
            if (post.PublishAt is not null && post.PublishAt > DateTime.UtcNow) return null;
            if (!await IsVisibleToAsync(newsId, viewerId, ct)) return null;
        }

        return await MapDetailAsync(newsId, viewerId, ct);
    }

    public async Task<PagedResult<NewsSummaryDto>> ListAsync(Guid viewerId, NewsListFilter filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 100);
        var now = DateTime.UtcNow;
        var canManage = await CanManageAsync(viewerId, ct);
        var view = filter.View?.Trim().ToLowerInvariant();

        IQueryable<NewsPost> q = db.NewsPosts.AsNoTracking();

        if (canManage && view == "drafts")
            // Legacy pending/rejected rows surface as drafts: there is no approval step anymore.
            q = q.Where(x => x.Status != "published");
        else if (canManage && view == "scheduled")
            q = q.Where(x => x.Status == "published" && x.PublishAt != null && x.PublishAt > now);
        else if (canManage && !string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(x => x.Status == filter.Status.ToLowerInvariant().Trim());
        else
        {
            // Reader feed (also the manager's default feed): live published posts only.
            q = q.Where(x => x.Status == "published");
            if (!(canManage && filter.IncludeScheduled))
                q = q.Where(x => x.PublishAt == null || x.PublishAt <= now);
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var type = filter.Type.ToLowerInvariant().Trim();
            q = type == "condolence" ? q.Where(x => x.Type == "condolence" || x.Type == "duka") : q.Where(x => x.Type == type);
        }
        if (filter.PinnedOnly == true) q = q.Where(x => x.IsPinned);
        if (filter.RequiresAckOnly == true)
        {
            var acked = db.NewsAcknowledgements.Where(a => a.UserId == viewerId).Select(a => a.NewsPostId);
            q = q.Where(x => x.RequiresAcknowledgement && !acked.Contains(x.Id));
        }
        if (view == "unread")
        {
            var read = db.NewsReads.Where(r => r.UserId == viewerId && r.ReadAt != null).Select(r => r.NewsPostId);
            q = q.Where(x => !read.Contains(x.Id));
        }
        else if (view == "saved")
        {
            var saved = db.NewsBookmarks.Where(b => b.UserId == viewerId).Select(b => b.NewsPostId);
            q = q.Where(x => saved.Contains(x.Id));
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pat = $"%{filter.Search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            // ILike is Postgres-only; SQLite in-memory (TestDb) needs LIKE fallback
            var useNpgsql = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
            q = useNpgsql
                ? q.Where(x => EF.Functions.ILike(x.Title, pat, @"\") || EF.Functions.ILike(x.Body, pat, @"\") || EF.Functions.ILike(x.Author.Name, pat, @"\"))
                : q.Where(x => EF.Functions.Like(x.Title, pat, @"\") || EF.Functions.Like(x.Body, pat, @"\") || EF.Functions.Like(x.Author.Name, pat, @"\"));
        }

        // HR/Admin see every post; readers only what targeting grants them.
        if (!canManage)
        {
            var visibleIds = await VisiblePostIdsAsync(viewerId, ct);
            q = q.Where(x => visibleIds.Contains(x.Id));
        }

        var total = await q.CountAsync(ct);
        var sort = filter.Sort?.Trim().ToLowerInvariant();
        IOrderedQueryable<NewsPost> ordered = sort switch
        {
            "oldest" => q.OrderBy(x => x.PublishedAt ?? x.CreatedAt),
            "mostread" => q.OrderByDescending(x => db.NewsReads.Count(r => r.NewsPostId == x.Id)).ThenByDescending(x => x.PublishedAt ?? x.CreatedAt),
            _ => q.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.PublishedAt ?? x.CreatedAt),
        };
        var items = await ordered
            .Skip((page - 1) * size).Take(size)
            .Include(x => x.Author)
            .Include(x => x.Targets)
            .ToListAsync(ct);

        var postIds = items.Select(x => x.Id).ToList();
        var ackedIds = new HashSet<Guid>(await db.NewsAcknowledgements.AsNoTracking()
            .Where(a => postIds.Contains(a.NewsPostId) && a.UserId == viewerId)
            .Select(a => a.NewsPostId).ToListAsync(ct));
        var readIds = new HashSet<Guid>(await db.NewsReads.AsNoTracking()
            .Where(r => postIds.Contains(r.NewsPostId) && r.UserId == viewerId && r.ReadAt != null)
            .Select(r => r.NewsPostId).ToListAsync(ct));
        var savedIds = new HashSet<Guid>(await db.NewsBookmarks.AsNoTracking()
            .Where(b => postIds.Contains(b.NewsPostId) && b.UserId == viewerId)
            .Select(b => b.NewsPostId).ToListAsync(ct));
        var views = await db.NewsReads.AsNoTracking().Where(r => postIds.Contains(r.NewsPostId))
            .GroupBy(r => r.NewsPostId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var files = await db.NewsAttachments.AsNoTracking()
            .Where(a => postIds.Contains(a.NewsPostId) && a.Attachment.DeletedAt == null)
            .GroupBy(a => a.NewsPostId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var videos = await VideoBriefsAsync(postIds, ct);

        var summaries = items.Select(p => new NewsSummaryDto(
            p.Id, p.Type, p.Title, NormalizeStatus(p.Status), p.Author.Name, p.Author.Nik,
            p.PublishAt, p.PublishedAt, p.IsPinned, p.PinnedAt, p.RequiresAcknowledgement,
            p.Targets.Select(t => new NewsTargetDto(t.Kind, t.Value)).ToList(),
            ackedIds.Contains(p.Id), p.CreatedAt, p.UpdatedAt,
            Summary: Excerpt(p.Body, 160),
            IsRead: readIds.Contains(p.Id),
            IsSaved: savedIds.Contains(p.Id),
            ViewCount: views.GetValueOrDefault(p.Id),
            AttachmentCount: files.GetValueOrDefault(p.Id),
            AckDueAt: p.AckDueAt,
            EditedAt: p.EditedAt,
            Video: videos.GetValueOrDefault(p.Id))).ToList();

        return new PagedResult<NewsSummaryDto>(summaries, total, page, size);
    }

    public async Task<NewsCountsDto> GetCountsAsync(Guid viewerId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var canManage = await CanManageAsync(viewerId, ct);
        var live = db.NewsPosts.AsNoTracking()
            .Where(x => x.Status == "published" && (x.PublishAt == null || x.PublishAt <= now));
        if (!canManage)
        {
            var visibleIds = await VisiblePostIdsAsync(viewerId, ct);
            live = live.Where(x => visibleIds.Contains(x.Id));
        }
        var read = db.NewsReads.Where(r => r.UserId == viewerId && r.ReadAt != null).Select(r => r.NewsPostId);
        var acked = db.NewsAcknowledgements.Where(a => a.UserId == viewerId).Select(a => a.NewsPostId);
        var saved = db.NewsBookmarks.Where(b => b.UserId == viewerId).Select(b => b.NewsPostId);

        var all = await live.CountAsync(ct);
        var unread = await live.CountAsync(x => !read.Contains(x.Id), ct);
        var needsAck = await live.CountAsync(x => x.RequiresAcknowledgement && !acked.Contains(x.Id), ct);
        var savedCount = await live.CountAsync(x => saved.Contains(x.Id), ct);
        var pinned = await live.CountAsync(x => x.IsPinned, ct);
        var scheduled = canManage
            ? await db.NewsPosts.AsNoTracking().CountAsync(x => x.Status == "published" && x.PublishAt != null && x.PublishAt > now, ct)
            : 0;
        var drafts = canManage ? await db.NewsPosts.AsNoTracking().CountAsync(x => x.Status != "published", ct) : 0;
        return new NewsCountsDto(all, unread, needsAck, savedCount, pinned, scheduled, drafts, canManage);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Reader state: read / unread, bookmarks
    // ──────────────────────────────────────────────────────────────────────

    public async Task MarkReadAsync(Guid userId, Guid newsId, bool read, CancellationToken ct)
    {
        var row = await db.NewsReads.FirstOrDefaultAsync(r => r.NewsPostId == newsId && r.UserId == userId, ct);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            if (!read) return;
            db.NewsReads.Add(new NewsRead { NewsPostId = newsId, UserId = userId, FirstReadAt = now, ReadAt = now });
        }
        else if (read && row.ReadAt is null) row.ReadAt = now;
        else if (!read && row.ReadAt is not null) row.ReadAt = null;
        else return;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* concurrent first-open from another tab: already recorded */ }
    }

    public async Task<bool> SetBookmarkAsync(Guid userId, Guid newsId, bool saved, CancellationToken ct)
    {
        var row = await db.NewsBookmarks.FirstOrDefaultAsync(b => b.NewsPostId == newsId && b.UserId == userId, ct);
        if (saved && row is null)
            db.NewsBookmarks.Add(new NewsBookmark { NewsPostId = newsId, UserId = userId, CreatedAt = DateTime.UtcNow });
        else if (!saved && row is not null)
            db.NewsBookmarks.Remove(row);
        else return saved;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* double click: the row already exists */ }
        return saved;
    }

    public async Task<bool> CanViewAsync(Guid userId, Guid newsId, CancellationToken ct)
        => await GetAsync(newsId, userId, ct) is not null;

    // ──────────────────────────────────────────────────────────────────────
    // Delete (author of the post, or HR/Admin; audited in the global trail since the rows go away)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> DeleteAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.AuthorId != actorId && !await CanManageAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var now = DateTime.UtcNow;
        var attachmentIds = await db.NewsAttachments.Where(a => a.NewsPostId == newsId).Select(a => a.AttachmentId).ToListAsync(ct);
        await db.Attachments.Where(a => attachmentIds.Contains(a.Id) && a.DeletedAt == null)
            .ForEachAsync(a => a.DeletedAt = now, ct);
        db.NewsPosts.Remove(post);
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), Action = "news.delete", Entity = "NewsPost", EntityId = newsId.ToString(),
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { actor = actorId, title = post.Title, type = post.Type, status = post.Status }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await BroadcastChangedAsync(ct);
        return new NewsResultDto(true, null, null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Acknowledgement report (HR/Admin)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsAckReportDto?> GetAckReportAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct)) return null;
        var post = await db.NewsPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null || !post.RequiresAcknowledgement) return null;

        var targeted = await GetTargetedUserIdsAsync(newsId, ct);
        var acks = await db.NewsAcknowledgements.AsNoTracking().Where(a => a.NewsPostId == newsId)
            .ToDictionaryAsync(a => a.UserId, a => a.AcknowledgedAt, ct);
        // Readers who acknowledged but later left the audience still count toward the record.
        var ids = targeted.Union(acks.Keys).ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Nik, u.Name, Dept = u.Department != null ? u.Department.Name : null, u.Position })
            .ToListAsync(ct);
        var entries = users
            .Select(u => new NewsAckEntryDto(u.Id, u.Nik, u.Name, u.Dept, u.Position, acks.TryGetValue(u.Id, out var at) ? at : null))
            .OrderBy(e => e.AcknowledgedAt is null ? 0 : 1).ThenBy(e => e.Name)
            .ToList();
        return new NewsAckReportDto(targeted.Count, targeted.Count(acks.ContainsKey), entries);
    }

    public async Task<NewsResultDto> RemindAckAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanManageAsync(actorId, ct))
            return new NewsResultDto(false, null, "forbidden");

        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "published" || !post.RequiresAcknowledgement)
            return new NewsResultDto(false, null, "invalid_state");

        if (notificationGateway is not null)
        {
            try
            {
                // Only readers who have not acknowledged yet get the reminder.
                var acked = await db.NewsAcknowledgements.AsNoTracking()
                    .Where(a => a.NewsPostId == post.Id).Select(a => a.UserId).ToListAsync(ct);
                var pending = (await GetTargetedUserIdsAsync(post.Id, ct)).Except(acked).ToList();
                if (pending.Count > 0)
                await notificationGateway.PublishInternalAsync(
                    clientSlug: "news",
                    eventType: "memo.reminder",
                    title: $"[Pengingat Wajib Baca] {post.Title}",
                    message: post.AckDueAt is { } due
                        ? $"Mohon baca dan konfirmasi memo ini sebelum {due:dd MMM yyyy}."
                        : "Mohon segera membaca dan mengonfirmasi pemahaman atas memo ini.",
                    target: new NotificationTarget(UserIds: pending),
                    priority: "high",
                    targetUrl: $"/news?post={post.Id}",
                    ct: ct);
            }
            catch
            {
                // Non-blocking notification delivery
            }
        }

        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "reminded_ack", CreatedAt = DateTime.UtcNow });
        AuditLog(post.Id, "news.remind_ack", actorId, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<IReadOnlyList<NewsAuditDto>> GetAuditsAsync(Guid newsId, Guid viewerId, CancellationToken ct)
    {
        // Only HR/Admin may view audits. Reader-only access is not granted.
        if (!await CanManageAsync(viewerId, ct)) return [];
        return await db.NewsAudits.AsNoTracking().Where(a => a.NewsPostId == newsId)
            .OrderBy(a => a.CreatedAt)
            .Include(a => a.Actor)
            .Select(a => new NewsAuditDto(a.Id, a.ActorId, a.Actor.Name, a.Action, a.Detail, a.CreatedAt))
            .ToListAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private readonly Dictionary<Guid, bool> _canManage = [];

    /// HR/Admin gate for every write. Admin role, or news.publish through RBAC (the HR role
    /// is seeded with it). news.write alone is not enough: the Employee baseline carries it.
    public async Task<bool> CanManageAsync(Guid userId, CancellationToken ct)
    {
        if (_canManage.TryGetValue(userId, out var cached)) return cached;
        var allowed = await IsAdminAsync(userId, ct)
            || (permEval is not null && await permEval.IsAllowedAsync(userId, "news.publish", null, ct))
            || await (from ur in db.UserRoles.AsNoTracking()
                      join rp in db.RolePermissions.AsNoTracking() on ur.RoleId equals rp.RoleId
                      join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                      where ur.UserId == userId && (p.Code == "news.publish" || p.Code == "news.approve")
                      select p.Id).AnyAsync(ct);
        _canManage[userId] = allowed;
        return allowed;
    }

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        return await db.UserRoles.AsNoTracking()
            .Include(ur => ur.Role).AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);
    }

    private async Task<bool> IsVisibleToAsync(Guid newsId, Guid userId, CancellationToken ct)
    {
        var targets = await db.NewsTargets.AsNoTracking().Where(t => t.NewsPostId == newsId).ToListAsync(ct);
        if (targets.Count == 0) return true; // "all"
        if (targets.Any(t => t.Kind == "all")) return true;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        if (targets.Any(t => t.Kind == "user" && t.Value == user.Nik)) return true;

        if (user.DepartmentId is not null)
        {
            var deptCode = await db.Departments.AsNoTracking().Where(d => d.Id == user.DepartmentId).Select(d => d.Code).FirstOrDefaultAsync(ct);
            if (deptCode is not null && targets.Any(t => t.Kind == "department" && t.Value == deptCode)) return true;
        }

        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Include(ur => ur.Role).Select(ur => ur.Role.Name).ToListAsync(ct);
        if (targets.Any(t => t.Kind == "role" && roles.Contains(t.Value!))) return true;

        // ipop-23 P2-1: project-targeted posts are visible to project members (live query)
        var projectTargets = targets
            .Where(t => t.Kind == "project" && t.Value is not null && Guid.TryParse(t.Value, out _))
            .Select(t => Guid.Parse(t.Value!))
            .ToList();
        if (projectTargets.Count > 0 &&
            await db.ProjectMembers.AsNoTracking().AnyAsync(pm => pm.UserId == userId && projectTargets.Contains(pm.ProjectId), ct))
            return true;

        return false;
    }

    private async Task<NewsDetailDto> MapDetailAsync(Guid newsId, Guid viewerId, CancellationToken ct)
    {
        var p = await db.NewsPosts.AsNoTracking()
            .Include(x => x.Author).Include(x => x.Approver)
            .Include(x => x.Targets)
            .FirstAsync(x => x.Id == newsId, ct);
        var audits = await db.NewsAudits.AsNoTracking().Where(a => a.NewsPostId == newsId)
            .OrderBy(a => a.CreatedAt).Include(a => a.Actor)
            .Select(a => new NewsAuditDto(a.Id, a.ActorId, a.Actor.Name, a.Action, a.Detail, a.CreatedAt))
            .ToListAsync(ct);
        var isAcked = await db.NewsAcknowledgements.AsNoTracking().AnyAsync(a => a.NewsPostId == newsId && a.UserId == viewerId, ct);
        var ackCount = await db.NewsAcknowledgements.AsNoTracking().CountAsync(a => a.NewsPostId == newsId, ct);
        var ackTotal = p.RequiresAcknowledgement ? (await GetTargetedUserIdsAsync(newsId, ct)).Count : 0;
        var ackedAt = await db.NewsAcknowledgements.AsNoTracking()
            .Where(a => a.NewsPostId == newsId && a.UserId == viewerId).Select(a => (DateTime?)a.AcknowledgedAt).FirstOrDefaultAsync(ct);
        var isSaved = await db.NewsBookmarks.AsNoTracking().AnyAsync(b => b.NewsPostId == newsId && b.UserId == viewerId, ct);
        var viewCount = await db.NewsReads.AsNoTracking().CountAsync(r => r.NewsPostId == newsId, ct);
        var attachments = await db.NewsAttachments.AsNoTracking()
            .Where(a => a.NewsPostId == newsId && a.Attachment.DeletedAt == null)
            .OrderBy(a => a.SortOrder)
            .Select(a => new NewsAttachmentDto(a.AttachmentId, a.Attachment.FileName, a.Attachment.ContentType, a.Attachment.Size))
            .ToListAsync(ct);
        var canManage = await CanManageAsync(viewerId, ct);
        var isAuthor = p.AuthorId == viewerId;
        return new NewsDetailDto(
            p.Id, p.Type, p.Title, p.Body, NormalizeStatus(p.Status), p.AuthorId, p.Author.Name, p.Author.Nik,
            p.ApproverId, p.Approver?.Name, p.RejectionReason,
            p.PublishAt, p.PublishedAt, p.IsPinned, p.PinnedAt, p.RequiresAcknowledgement,
            p.Targets.Select(t => new NewsTargetDto(t.Kind, t.Value)).ToList(),
            canManage ? audits : [], isAcked, ackCount, ackTotal, p.CreatedAt, p.UpdatedAt,
            AckDueAt: p.AckDueAt,
            AcknowledgedAt: ackedAt,
            EditedAt: p.EditedAt,
            IsSaved: isSaved,
            ViewCount: viewCount,
            Attachments: attachments,
            CanManage: canManage,
            CanEdit: canManage || isAuthor,
            CanDelete: canManage || isAuthor,
            CanPublish: canManage,
            Video: (await VideoBriefsAsync([newsId], ct)).GetValueOrDefault(newsId));
    }

    /// Launch videos attached to these posts (one per post), for the feed card and detail player.
    private Task<Dictionary<Guid, NewsVideoBriefDto>> VideoBriefsAsync(List<Guid> postIds, CancellationToken ct) =>
        db.NewsVideos.AsNoTracking().Where(v => postIds.Contains(v.NewsPostId))
            .ToDictionaryAsync(v => v.NewsPostId,
                v => new NewsVideoBriefDto(v.Id, v.DurationSeconds, v.ThumbnailPath != null, v.FileName, v.Size), ct);

    /// Active users in the post's audience ("all" when it has no targets).
    private async Task<HashSet<Guid>> GetTargetedUserIdsAsync(Guid newsId, CancellationToken ct)
    {
        var targets = await db.NewsTargets.AsNoTracking().Where(t => t.NewsPostId == newsId).ToListAsync(ct);
        var active = db.Users.AsNoTracking().Where(u => u.IsActive);
        if (targets.Count == 0 || targets.Any(t => t.Kind == "all"))
            return (await active.Select(u => u.Id).ToListAsync(ct)).ToHashSet();

        var ids = new HashSet<Guid>();
        var niks = targets.Where(t => t.Kind == "user").Select(t => t.Value).ToList();
        var depts = targets.Where(t => t.Kind == "department").Select(t => t.Value).ToList();
        var roles = targets.Where(t => t.Kind == "role").Select(t => t.Value).ToList();
        var projects = targets.Where(t => t.Kind == "project" && Guid.TryParse(t.Value, out _)).Select(t => Guid.Parse(t.Value!)).ToList();
        if (niks.Count > 0)
            ids.UnionWith(await active.Where(u => niks.Contains(u.Nik)).Select(u => u.Id).ToListAsync(ct));
        if (depts.Count > 0)
            ids.UnionWith(await active.Where(u => u.Department != null && depts.Contains(u.Department.Code)).Select(u => u.Id).ToListAsync(ct));
        if (roles.Count > 0)
            ids.UnionWith(await db.UserRoles.AsNoTracking().Where(ur => roles.Contains(ur.Role.Name) && ur.User.IsActive).Select(ur => ur.UserId).ToListAsync(ct));
        if (projects.Count > 0)
            ids.UnionWith(await db.ProjectMembers.AsNoTracking().Where(pm => projects.Contains(pm.ProjectId) && pm.User.IsActive).Select(pm => pm.UserId).ToListAsync(ct));
        return ids;
    }

    /// Post ids the reader may see through targeting (untargeted posts are for everyone).
    private async Task<List<Guid>> VisiblePostIdsAsync(Guid viewerId, CancellationToken ct)
    {
        var viewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == viewerId, ct);
        if (viewer is null) return [];
        var viewerRoleNames = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == viewerId).Select(ur => ur.Role.Name).ToListAsync(ct);
        var deptCode = viewer.DepartmentId is null ? null
            : await db.Departments.AsNoTracking().Where(d => d.Id == viewer.DepartmentId).Select(d => d.Code).FirstOrDefaultAsync(ct);
        // ipop-23 P2-1: viewer's project memberships as "D"-format GUID strings (matches NewsTarget.Value)
        var projectValues = (await db.ProjectMembers.AsNoTracking()
            .Where(pm => pm.UserId == viewerId).Select(pm => pm.ProjectId).ToListAsync(ct))
            .Select(id => id.ToString("D")).ToList();

        var targeted = db.NewsTargets.Select(t => t.NewsPostId);
        return await db.NewsPosts.AsNoTracking()
            .Where(p => !targeted.Contains(p.Id) || db.NewsTargets.Any(t => t.NewsPostId == p.Id && (
                t.Kind == "all" ||
                (t.Kind == "user" && t.Value == viewer.Nik) ||
                (t.Kind == "department" && deptCode != null && t.Value == deptCode) ||
                (t.Kind == "role" && viewerRoleNames.Contains(t.Value!)) ||
                (t.Kind == "project" && projectValues.Contains(t.Value!)))))
            .Select(p => p.Id).ToListAsync(ct);
    }

    /// Links uploaded "news" attachments to the post (full replace). Only files still in the
    /// news area and not linked elsewhere may be attached; detached files are soft-deleted.
    private async Task<bool> ReplaceAttachmentsAsync(Guid newsId, IReadOnlyList<Guid>? attachmentIds, Guid actorId, CancellationToken ct)
    {
        if (attachmentIds is null) return false;
        var wanted = attachmentIds.Distinct().ToList();
        var existing = await db.NewsAttachments.Where(a => a.NewsPostId == newsId).ToListAsync(ct);
        var linkedElsewhere = await db.NewsAttachments.AsNoTracking()
            .Where(a => wanted.Contains(a.AttachmentId) && a.NewsPostId != newsId).Select(a => a.AttachmentId).ToListAsync(ct);
        var valid = await db.Attachments.AsNoTracking()
            .Where(a => wanted.Contains(a.Id) && a.Area == "news" && a.DeletedAt == null && !linkedElsewhere.Contains(a.Id))
            .Select(a => a.Id).ToListAsync(ct);
        var ordered = wanted.Where(valid.Contains).ToList();

        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var row in existing.Where(e => !ordered.Contains(e.AttachmentId)))
        {
            db.NewsAttachments.Remove(row);
            var file = await db.Attachments.FirstOrDefaultAsync(a => a.Id == row.AttachmentId, ct);
            if (file is not null && file.DeletedAt is null) file.DeletedAt = now;
            changed = true;
        }
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = existing.FirstOrDefault(e => e.AttachmentId == ordered[i]);
            if (row is null)
            {
                db.NewsAttachments.Add(new NewsAttachment { NewsPostId = newsId, AttachmentId = ordered[i], SortOrder = i });
                changed = true;
            }
            else if (row.SortOrder != i) { row.SortOrder = i; changed = true; }
        }
        return changed;
    }

    /// Legacy approval states surface as drafts; the direct-publish flow has no review step.
    private static string NormalizeStatus(string status) => status is "pending" or "rejected" ? "draft" : status;

    private static string Excerpt(string body, int max)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(body ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    private async Task ReplaceTargetsAsync(Guid newsId, IReadOnlyList<NewsTargetInput>? inputs, CancellationToken ct)
    {
        var existing = await db.NewsTargets.Where(t => t.NewsPostId == newsId).ToListAsync(ct);
        db.NewsTargets.RemoveRange(existing);
        if (inputs is null || inputs.Count == 0) return;
        foreach (var inp in inputs)
        {
            var kind = inp.Kind.ToLowerInvariant().Trim();
            var val = inp.Value?.Trim();
            if (kind == "all") val = null;
            // ipop-23 P2-1: normalize project GUID to canonical "D" format so list-side string comparison matches
            if (kind == "project" && val is not null && Guid.TryParse(val, out var g)) val = g.ToString("D");
            db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = newsId, Kind = kind, Value = val });
        }
    }

    private static string? ValidateCreate(CreateNewsRequest req)
    {
        if (!ValidTypes.Contains(req.Type.ToLowerInvariant().Trim())) return "invalid_type";
        if (string.IsNullOrWhiteSpace(req.Title)) return "title_required";
        if (req.Title.Trim().Length > 300) return "title_too_long";
        if (string.IsNullOrWhiteSpace(req.Body)) return "body_required";
        return null;
    }

    private static string? ValidateTargets(IReadOnlyList<NewsTargetInput>? targets)
    {
        if (targets is null || targets.Count == 0) return null;
        foreach (var t in targets)
        {
            var kind = t.Kind.ToLowerInvariant().Trim();
            if (!ValidTargetKinds.Contains(kind)) return "invalid_target_kind";
            if (kind != "all" && string.IsNullOrWhiteSpace(t.Value)) return "target_value_required";
            if (kind == "all" && !string.IsNullOrWhiteSpace(t.Value)) return "target_value_forbidden_for_all";
            if (kind == "project" && !Guid.TryParse(t.Value, out _)) return "invalid_target_value";
        }
        return null;
    }

    private void AuditLog(Guid newsId, string action, Guid actorId, DateTime now)
    {
        // Best-effort mirror into global AuditLogs (for admin audit trail) — keep news-audits canonical
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), Action = action, Entity = "NewsPost", EntityId = newsId.ToString(),
            DetailJson = $"{{\"actor\":\"{actorId}\"}}", CreatedAt = now
        });
    }

    /// NewsChanged to everyone: payload-less refresh hint, each client re-fetches its own
    /// (visibility-filtered) list over REST. Best-effort — never fails the request.
    private async Task BroadcastChangedAsync(CancellationToken ct)
    {
        if (realtime is null) return;
        try { await realtime.ToAllAsync(RealtimeEvents.NewsChanged, ct: ct); }
        catch { /* realtime is a hint */ }
    }

    /// Per-user state (acknowledgement) changed: only that user's other tabs need to resync.
    private async Task NotifyUserChangedAsync(Guid userId, CancellationToken ct)
    {
        if (realtime is null) return;
        try
        {
            var nik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(nik)) await realtime.ToUserAsync(nik, RealtimeEvents.NewsChanged, ct: ct);
        }
        catch { /* realtime is a hint */ }
    }

    private async Task NotifyPublishedAsync(NewsPost post, CancellationToken ct)
    {
        if (notificationGateway is null) return;
        try
        {
            var isCondolence = post.Type is "condolence" or "duka";
            var isMemo = post.Type == "memo";
            var isMandatory = post.RequiresAcknowledgement;
            var eventType = isCondolence ? "news.condolence" : (isMandatory ? "memo.mandatory" : (isMemo ? "memo.published" : "news.published"));
            var titlePrefix = isCondolence ? "[Berita Duka] " : (isMandatory ? "[Wajib Baca] " : (isMemo ? "[Memo] " : "[Berita] "));
            var priority = (isMandatory || isCondolence) ? "high" : "normal";
            var preview = post.Title.Trim();
            var snippet = post.Body?.Length > 120 ? post.Body[..120] + "..." : post.Body;

            await notificationGateway.PublishInternalAsync(
                clientSlug: "news",
                eventType: eventType,
                title: $"{titlePrefix}{preview}",
                message: string.IsNullOrWhiteSpace(snippet) ? preview : snippet,
                target: new NotificationTarget(All: true),
                priority: priority,
                targetUrl: $"/news/{post.Id}",
                ct: ct);
        }
        catch
        {
            // Non-blocking notification delivery
        }
    }
}
