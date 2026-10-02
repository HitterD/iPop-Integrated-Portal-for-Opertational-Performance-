using IPop.Application.Attachments;
using IPop.Application.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.News;

/// News files are readable by anyone who can see the post they're attached to. Files not yet
/// linked (uploaded in the editor, post not saved) are readable only by their uploader and HR/Admin.
public sealed class NewsAttachmentPolicy(IPopDbContext db, INewsService news) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.News;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct)) return false;
        var mediaIds = await db.NewsComposerMedia.AsNoTracking()
            .Where(m => m.AttachmentId == attachmentId && m.DeletedAt == null)
            .Select(m => m.Id).ToListAsync(ct);
        if (mediaIds.Count > 0)
        {
            var posts = await db.NewsPostMedia.AsNoTracking()
                .Where(r => mediaIds.Contains(r.MediaId)).Select(r => r.NewsPostId).Distinct().ToListAsync(ct);
            foreach (var id in posts)
                if (await news.CanViewAsync(userId, id, ct)) return true;
            var drafts = await db.NewsComposerDrafts.AsNoTracking()
                .Where(d => d.OwnerId == userId && d.State != "discarded" && d.State != "promoted"
                    && (db.NewsComposerDraftMedia.Any(r => r.DraftId == d.Id && mediaIds.Contains(r.MediaId))
                        || db.NewsComposerMedia.Any(m => mediaIds.Contains(m.Id) && m.UploadDraftId == d.Id && m.OwnerId == userId)))
                .Select(d => d.PostId).ToListAsync(ct);
            foreach (var id in drafts)
                if (id is { } post ? await news.GetAsync(post, userId, ct) is { CanEdit: true } : await news.CanManageAsync(userId, ct))
                    return true;
            return false;
        }

        var postId = await db.NewsAttachments.AsNoTracking()
            .Where(a => a.AttachmentId == attachmentId).Select(a => (Guid?)a.NewsPostId).FirstOrDefaultAsync(ct);
        if (postId is not null) return await news.CanViewAsync(userId, postId.Value, ct);

        var uploader = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId).Select(a => a.UploaderId).FirstOrDefaultAsync(ct);
        var privateDrafts = await db.NewsComposerDraftAttachments.AsNoTracking()
            .Where(r => r.AttachmentId == attachmentId && r.Draft.State != "discarded" && r.Draft.State != "promoted")
            .Select(r => new { r.Draft.OwnerId, r.Draft.PostId }).ToListAsync(ct);
        if (privateDrafts.Count > 0)
        {
            foreach (var draft in privateDrafts.Where(d => d.OwnerId == userId))
                if (draft.PostId is { } post ? await news.GetAsync(post, userId, ct) is { CanEdit: true } : await news.CanManageAsync(userId, ct))
                    return true;
            return false;
        }
        return uploader == userId || await news.CanManageAsync(userId, ct);
    }
}
