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

        // Linked to several pages (the same image pasted into a second page): any page the user can open is enough.
        var pageIds = await db.NoteAttachments.AsNoTracking()
            .Where(a => a.AttachmentId == attachmentId).Select(a => a.PageId).ToListAsync(ct);
        foreach (var pageId in pageIds)
            if (await notes.GetAccessAsync(userId, pageId, ct) > NoteAccess.None) return true;
        if (pageIds.Count > 0 && att.UploaderId != userId) return false;

        return att.UploaderId == userId;
    }
}
