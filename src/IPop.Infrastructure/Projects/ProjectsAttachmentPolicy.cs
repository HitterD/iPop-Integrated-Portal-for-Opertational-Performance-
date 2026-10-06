using IPop.Application.Attachments;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// A task file is readable by every member of the task's project while its link is live; a removed
/// link closes it for everyone. An upload no task links to is readable only by its uploader.
public sealed class ProjectsAttachmentPolicy(IPopDbContext db) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.Projects;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        // IgnoreQueryFilters: a task in the bin keeps its files reachable for members (Undo restores it).
        var link = await db.ProjectTaskFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.AttachmentId == attachmentId)
            .Select(f => new { f.DeletedAt, f.Task.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (link is not null)
            return link.DeletedAt is null
                && await db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == link.ProjectId && m.UserId == userId, ct);

        var uploader = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId).Select(a => (Guid?)a.UploaderId).FirstOrDefaultAsync(ct);
        return uploader == userId;
    }
}
