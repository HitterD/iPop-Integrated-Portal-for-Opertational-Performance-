using IPop.Application.Attachments;
using IPop.Application.Notes;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Files in a note are readable by anyone who can open the page; an unlinked upload only by its uploader.
public sealed class NotesAttachmentPolicy(IPopDbContext db, INoteService notes) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.Notes;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        var att = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId && a.DeletedAt == null)
            .Select(a => new { a.UploaderId }).FirstOrDefaultAsync(ct);
        if (att is null) return false;

        var pageId = await db.NoteAttachments.AsNoTracking()
            .Where(a => a.AttachmentId == attachmentId).Select(a => (Guid?)a.PageId).FirstOrDefaultAsync(ct);
        if (pageId is not null) return await notes.GetAccessAsync(userId, pageId.Value, ct) > NoteAccess.None;



        return att.UploaderId == userId;
    }
}
