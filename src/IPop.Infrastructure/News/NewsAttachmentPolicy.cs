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
        var postId = await db.NewsAttachments.AsNoTracking()
            .Where(a => a.AttachmentId == attachmentId).Select(a => (Guid?)a.NewsPostId).FirstOrDefaultAsync(ct);
        if (postId is not null) return await news.CanViewAsync(userId, postId.Value, ct);

        var uploader = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId).Select(a => a.UploaderId).FirstOrDefaultAsync(ct);
        return uploader == userId || await news.CanManageAsync(userId, ct);
    }
}
