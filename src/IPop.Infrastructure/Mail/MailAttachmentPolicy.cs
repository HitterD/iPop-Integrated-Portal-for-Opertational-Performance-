using IPop.Application.Attachments;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Mail;

/// Images pasted into a mail body (area "mail") are readable by everyone on a mail that shows
/// them — any recipient row, the sender's own "from" row included — unless that mail was
/// recalled. The uploader can always read their own image (e.g. in a recalled mail they still see in Sent).
public sealed class MailAttachmentPolicy(IPopDbContext db) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.Mail;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        if (await MailInlineImages.ReadableLinks(db, userId).AnyAsync(l => l.AttachmentId == attachmentId, ct)) return true;
        return await db.Attachments.AsNoTracking()
            .AnyAsync(a => a.Id == attachmentId && a.UploaderId == userId, ct);
    }
}
