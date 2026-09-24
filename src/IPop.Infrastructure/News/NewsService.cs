using IPop.Application.News;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.News;

using IPop.Application.Rbac;

public sealed class NewsService(IPopDbContext db, INotificationGatewayService? notificationGateway = null, IPermissionEvaluator? permEval = null) : INewsService
{
    private static readonly HashSet<string> ValidTypes = ["news", "announcement", "memo", "condolence", "duka"];
    private static readonly HashSet<string> ValidStatuses = ["draft", "pending", "published", "rejected"];
    private static readonly HashSet<string> ValidTargetKinds = ["all", "department", "role", "user", "project"];

    // ──────────────────────────────────────────────────────────────────────
    // Create
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> CreateAsync(Guid authorId, CreateNewsRequest req, CancellationToken ct)
    {
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
        await db.SaveChangesAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, authorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Update (only draft or rejected may be edited)
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> UpdateAsync(Guid actorId, Guid newsId, UpdateNewsRequest req, CancellationToken ct)
    {
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.AuthorId != actorId && !await IsAdminAsync(actorId, ct))
            return new NewsResultDto(false, null, "forbidden");
        if (post.Status is "pending" or "published")
            return new NewsResultDto(false, null, "invalid_transition");

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
        if (req.Title is not null) { post.Title = req.Title.Trim(); changed = true; }
        if (req.Body is not null) { post.Body = req.Body; changed = true; }
        if (req.Type is not null) { post.Type = req.Type.ToLowerInvariant().Trim(); changed = true; }
        if (req.PublishAt is not null) { post.PublishAt = req.PublishAt; changed = true; }
        if (req.RequiresAcknowledgement is not null) { post.RequiresAcknowledgement = req.RequiresAcknowledgement.Value; changed = true; }
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
            // Rejected -> draft on any edit so it can be resubmitted
            if (post.Status == "rejected") post.Status = "draft";
            db.NewsAudits.Add(new NewsAudit
            {
                Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId,
                Action = "edited", CreatedAt = post.UpdatedAt
            });
            AuditLog(post.Id, "news.edit", actorId, post.UpdatedAt);
            await db.SaveChangesAsync(ct);
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
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> ApproveAsync(Guid approverId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(approverId, ct)) return new NewsResultDto(false, null, "forbidden");
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
        await NotifyPublishedAsync(post, ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, approverId, ct), null);
    }

    public async Task<NewsResultDto> RejectAsync(Guid approverId, Guid newsId, string reason, CancellationToken ct)
    {
        if (!await CanPublishAsync(approverId, ct)) return new NewsResultDto(false, null, "forbidden");
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
        return new NewsResultDto(true, await MapDetailAsync(post.Id, approverId, ct), null);
    }

    public async Task<NewsResultDto> PublishAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status == "published") return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
        if (post.Status != "pending") return new NewsResultDto(false, null, "invalid_transition");

        post.Status = "published";
        post.ApproverId = actorId;
        post.PublishedAt = DateTime.UtcNow;
        post.UpdatedAt = post.PublishedAt.Value;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "published", Detail = post.PublishAt?.ToString("O"), CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.publish", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await NotifyPublishedAsync(post, ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> UnpublishAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "published") return new NewsResultDto(false, null, "invalid_transition");

        post.Status = "draft";
        post.PublishedAt = null;
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "unpublished", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.unpublish", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Pin
    // ──────────────────────────────────────────────────────────────────────

    public async Task<NewsResultDto> PinAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        post.IsPinned = true;
        post.PinnedAt = DateTime.UtcNow;
        post.UpdatedAt = post.PinnedAt.Value;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "pinned", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.pin", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return new NewsResultDto(true, await MapDetailAsync(post.Id, actorId, ct), null);
    }

    public async Task<NewsResultDto> UnpinAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(actorId, ct)) return new NewsResultDto(false, null, "forbidden");
        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        post.IsPinned = false;
        post.PinnedAt = null;
        post.UpdatedAt = DateTime.UtcNow;
        db.NewsAudits.Add(new NewsAudit { Id = Guid.NewGuid(), NewsPostId = post.Id, ActorId = actorId, Action = "unpinned", CreatedAt = post.UpdatedAt });
        AuditLog(post.Id, "news.unpin", actorId, post.UpdatedAt);
        await db.SaveChangesAsync(ct);
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
        var isAuthorOrAdmin = await IsAuthorOrAdminAsync(newsId, viewerId, ct);
        if (!isAuthorOrAdmin)
        {
            // Reader must be in targeting + post must be published and not future-scheduled
            var post = await db.NewsPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == newsId, ct);
            if (post is null) return null;
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
        var isAdmin = await IsAdminAsync(viewerId, ct);

        IQueryable<NewsPost> q = db.NewsPosts.AsNoTracking();

        // Non-admin reader feed: only published + not future-scheduled + visible by targeting
        if (!isAdmin || filter.Status is null)
        {
            // If caller explicitly filtered by status, respect it (admin moderation view)
            if (!string.IsNullOrWhiteSpace(filter.Status))
                q = q.Where(x => x.Status == filter.Status.ToLowerInvariant().Trim());
            else if (!isAdmin)
            {
                q = q.Where(x => x.Status == "published");
                if (!filter.IncludeScheduled)
                    q = q.Where(x => x.PublishAt == null || x.PublishAt <= now);
            }
        }
        else
        {
            q = q.Where(x => x.Status == filter.Status.ToLowerInvariant().Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
            q = q.Where(x => x.Type == filter.Type.ToLowerInvariant().Trim());
        if (filter.PinnedOnly == true) q = q.Where(x => x.IsPinned);
        if (filter.RequiresAckOnly == true) q = q.Where(x => x.RequiresAcknowledgement);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pat = $"%{filter.Search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            // ILike is Postgres-only; SQLite in-memory (TestDb) needs LIKE fallback
            var useNpgsql = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
            q = useNpgsql
                ? q.Where(x => EF.Functions.ILike(x.Title, pat, @"\") || EF.Functions.ILike(x.Body, pat, @"\"))
                : q.Where(x => EF.Functions.Like(x.Title, pat, @"\") || EF.Functions.Like(x.Body, pat, @"\"));
        }

        // Targeting filter for non-admin readers: posts that are either "all" or targeted to the viewer
        // For admin / moderation listing we return everything (admin sees all).
        List<Guid>? visibleIds = null;
        if (!isAdmin)
        {
            var viewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == viewerId, ct);
            var viewerRoleNames = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == viewerId).Include(ur => ur.Role).Select(ur => ur.Role.Name).ToListAsync(ct);

            // ipop-23 P2-1: viewer's project memberships as "D"-format GUID strings (matches NewsTarget.Value)
            var viewerProjectIds = await db.ProjectMembers.AsNoTracking()
                .Where(pm => pm.UserId == viewerId)
                .Select(pm => pm.ProjectId)
                .ToListAsync(ct);
            var projectTargetValueStrings = viewerProjectIds.Select(id => id.ToString("D")).ToHashSet();
            var allIds = await db.NewsTargets.AsNoTracking().Select(t => t.NewsPostId).Distinct().ToListAsync(ct);
            var targetedIds = new HashSet<Guid>(allIds);

            // Posts with no targets are "all"
            var unTargetedIds = await db.NewsPosts.AsNoTracking()
                .Where(p => !allIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);

            var viewerTargets = await db.NewsTargets.AsNoTracking()
                .Where(t =>
                    t.Kind == "all" ||
                    (t.Kind == "user" && t.Value == viewer!.Nik) ||
                    (t.Kind == "department" && viewer!.DepartmentId != null && t.Value == db.Departments.Where(d => d.Id == viewer.DepartmentId).Select(d => d.Code).FirstOrDefault()) ||
                    (t.Kind == "role" && viewerRoleNames.Contains(t.Value!)) ||
                    (t.Kind == "project" && projectTargetValueStrings.Contains(t.Value!)))
                .Select(t => t.NewsPostId).Distinct().ToListAsync(ct);

            visibleIds = unTargetedIds.Concat(viewerTargets).Distinct().ToList();
            q = q.Where(x => visibleIds.Contains(x.Id));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.PublishedAt ?? x.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Include(x => x.Author)
            .Include(x => x.Targets)
            .ToListAsync(ct);

        // Bulk ack check
        var postIds = items.Select(x => x.Id).ToList();
        var ackedIds = new HashSet<Guid>(await db.NewsAcknowledgements.AsNoTracking()
            .Where(a => postIds.Contains(a.NewsPostId) && a.UserId == viewerId)
            .Select(a => a.NewsPostId).ToListAsync(ct));

        var summaries = items.Select(p => new NewsSummaryDto(
            p.Id, p.Type, p.Title, p.Status, p.Author.Name, p.Author.Nik,
            p.PublishAt, p.PublishedAt, p.IsPinned, p.PinnedAt, p.RequiresAcknowledgement,
            p.Targets.Select(t => new NewsTargetDto(t.Kind, t.Value)).ToList(),
            ackedIds.Contains(p.Id), p.CreatedAt, p.UpdatedAt)).ToList();

        return new PagedResult<NewsSummaryDto>(summaries, total, page, size);
    }

    public async Task<NewsResultDto> RemindAckAsync(Guid actorId, Guid newsId, CancellationToken ct)
    {
        if (!await CanPublishAsync(actorId, ct) && !await IsAuthorOrAdminAsync(newsId, actorId, ct))
            return new NewsResultDto(false, null, "forbidden");

        var post = await db.NewsPosts.FirstOrDefaultAsync(x => x.Id == newsId, ct);
        if (post is null) return new NewsResultDto(false, null, "not_found");
        if (post.Status != "published" || !post.RequiresAcknowledgement)
            return new NewsResultDto(false, null, "invalid_state");

        if (notificationGateway is not null)
        {
            try
            {
                await notificationGateway.PublishInternalAsync(
                    clientSlug: "news",
                    eventType: "memo.reminder",
                    title: $"[Pengingat Wajib Baca] {post.Title}",
                    message: "Mohon segera membaca dan mengonfirmasi pemahaman atas memo ini.",
                    target: new NotificationTarget(All: true),
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
        // Only author or admin may view audits. Reader-only access is not granted.
        if (!await IsAuthorOrAdminAsync(newsId, viewerId, ct)) return [];
        return await db.NewsAudits.AsNoTracking().Where(a => a.NewsPostId == newsId)
            .OrderBy(a => a.CreatedAt)
            .Include(a => a.Actor)
            .Select(a => new NewsAuditDto(a.Id, a.ActorId, a.Actor.Name, a.Action, a.Detail, a.CreatedAt))
            .ToListAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task<bool> CanPublishAsync(Guid userId, CancellationToken ct)
    {
        if (await IsAdminAsync(userId, ct)) return true;
        if (permEval is not null && await permEval.IsAllowedAsync(userId, "news.publish", null, ct))
            return true;
        return await (from ur in db.UserRoles.AsNoTracking()
                      join rp in db.RolePermissions.AsNoTracking() on ur.RoleId equals rp.RoleId
                      join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                      where ur.UserId == userId && (p.Code == "news.publish" || p.Code == "news.approve")
                      select p.Id).AnyAsync(ct);
    }

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        return await db.UserRoles.AsNoTracking()
            .Include(ur => ur.Role).AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);
    }

    private async Task<bool> IsAuthorOrAdminAsync(Guid newsId, Guid userId, CancellationToken ct)
    {
        if (await IsAdminAsync(userId, ct)) return true;
        return await db.NewsPosts.AsNoTracking().AnyAsync(x => x.Id == newsId && x.AuthorId == userId, ct);
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
        var ackTotal = 0;
        if (p.RequiresAcknowledgement)
        {
            var t = p.Targets;
            if (t.Count == 0 || t.Any(x => x.Kind == "all"))
                ackTotal = await db.Users.AsNoTracking().CountAsync(u => u.IsActive, ct);
            else
                ackTotal = await CountTargetedUsersAsync(newsId, ct);
        }
        return new NewsDetailDto(
            p.Id, p.Type, p.Title, p.Body, p.Status, p.AuthorId, p.Author.Name, p.Author.Nik,
            p.ApproverId, p.Approver?.Name, p.RejectionReason,
            p.PublishAt, p.PublishedAt, p.IsPinned, p.PinnedAt, p.RequiresAcknowledgement,
            p.Targets.Select(t => new NewsTargetDto(t.Kind, t.Value)).ToList(),
            audits, isAcked, ackCount, ackTotal, p.CreatedAt, p.UpdatedAt);
    }

    private async Task<int> CountTargetedUsersAsync(Guid newsId, CancellationToken ct)
    {
        var targets = await db.NewsTargets.AsNoTracking().Where(t => t.NewsPostId == newsId).ToListAsync(ct);
        var ids = new HashSet<Guid>();
        foreach (var t in targets)
        {
            if (t.Kind == "user")
            {
                var uid = await db.Users.AsNoTracking().Where(u => u.Nik == t.Value).Select(u => u.Id).FirstOrDefaultAsync(ct);
                if (uid != Guid.Empty) ids.Add(uid);
            }
            else if (t.Kind == "department")
            {
                var uids = await db.Users.AsNoTracking()
                    .Where(u => u.Department != null && u.Department.Code == t.Value)
                    .Select(u => u.Id).ToListAsync(ct);
                foreach (var id in uids) ids.Add(id);
            }
            else if (t.Kind == "role")
            {
                var uids = await db.UserRoles.AsNoTracking()
                    .Where(ur => ur.Role.Name == t.Value).Select(ur => ur.UserId).ToListAsync(ct);
                foreach (var id in uids) ids.Add(id);
            }
            else if (t.Kind == "project" && Guid.TryParse(t.Value, out var pid))
            {
                var uids = await db.ProjectMembers.AsNoTracking()
                    .Where(pm => pm.ProjectId == pid)
                    .Select(pm => pm.UserId).ToListAsync(ct);
                foreach (var id in uids) ids.Add(id);
            }
        }
        return ids.Count;
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
                targetUrl: "/news",
                ct: ct);
        }
        catch
        {
            // Non-blocking notification delivery
        }
    }
}
