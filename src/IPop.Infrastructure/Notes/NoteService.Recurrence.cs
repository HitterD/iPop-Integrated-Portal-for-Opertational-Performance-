using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

public sealed partial class NoteService
{
    private static NoteRecurrenceDto Dto(NoteRecurrence r) =>
        new(r.PageId, r.Rule, NoteRecurrenceRules.Describe(r.Rule), r.Enabled, r.StartsOn, r.LastCreatedFor, r.LastPageId);

    public async Task<NoteResult<NoteRecurrenceDto?>> GetRecurrenceAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<NoteRecurrenceDto?>("not_found");
        var r = await db.NoteRecurrences.AsNoTracking().FirstOrDefaultAsync(x => x.PageId == pageId && x.Enabled, ct);
        return Ok<NoteRecurrenceDto?>(r is null ? null : Dto(r));
    }

    public async Task<NoteResult<NoteRecurrenceDto>> SetRecurrenceAsync(Guid userId, Guid pageId, SetNoteRecurrenceRequest req, DateOnly today, CancellationToken ct)
    {
        var (page, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (page is null) return Fail<NoteRecurrenceDto>(Denied(access));
        if (page.Kind != "page" || page.IsGuide) return Fail<NoteRecurrenceDto>("not_a_page");
        var rule = NoteRecurrenceRules.FromChoice(req.Kind, req.Days, req.MonthDay);
        if (rule is null) return Fail<NoteRecurrenceDto>("invalid_rule");

        var existing = await db.NoteRecurrences.FirstOrDefaultAsync(x => x.PageId == pageId, ct);
        if (existing is null)
        {
            var mine = await db.NoteRecurrences.CountAsync(x => x.OwnerId == userId && x.Enabled, ct);
            if (mine >= NoteRecurrenceRules.MaxRulesPerPerson) return Fail<NoteRecurrenceDto>("too_many");
            existing = new NoteRecurrence { Id = Guid.NewGuid(), PageId = pageId, OwnerId = userId, Rule = rule, StartsOn = today, Enabled = true, CreatedAt = DateTime.UtcNow };
            db.NoteRecurrences.Add(existing);
        }
        else
        {
            // Changed, or switched back on: the new rule starts today (it never back-fills), and whoever changed it now owns what it makes.
            // A page already made for today stays made for today (LastCreatedFor is kept).
            existing.Rule = rule; existing.Enabled = true; existing.OwnerId = userId; existing.StartsOn = today;
        }
        await db.SaveChangesAsync(ct);
        return Ok(Dto(existing));
    }

    public async Task<NoteResult<bool>> StopRecurrenceAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<bool>(Denied(access));
        await db.NoteRecurrences.Where(x => x.PageId == pageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), ct);
        return Ok(true);
    }

    public async Task<NoteResult<NoteRecurrenceRunDto>> CreateRecurringCopyAsync(Guid templatePageId, Guid ownerId, string title, CancellationToken ct)
    {
        var src = await db.NotePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == templatePageId && p.DeletedAt == null, ct);
        if (src is null) return Fail<NoteRecurrenceRunDto>("not_found");
        // The person who set the repeat up must still be able to edit the template; a page in Trash (or under a trashed parent) makes nothing.
        if (await GetAccessAsync(ownerId, templatePageId, ct) < NoteAccess.Edit) return Fail<NoteRecurrenceRunDto>("forbidden");

        var now = DateTime.UtcNow;
        var copy = new NotePage
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, ParentId = src.ParentId,
            Title = Clean(title, 200) ?? "Untitled", Emoji = src.Emoji, Kind = src.Kind, Cover = src.Cover, CoverPosY = src.CoverPosY, Accent = src.Accent,
            Font = src.Font, FullWidth = src.FullWidth, SmallText = src.SmallText,
            YState = src.YState, Excerpt = src.Excerpt, PlainText = src.PlainText,
            TodoDone = src.TodoDone, TodoTotal = src.TodoTotal,
            CreatedAt = now, UpdatedAt = now, UpdatedById = ownerId,
        };
        db.NotePages.Add(copy);

        // Files inside the template are cloned for the new page, like Duplicate does.
        var srcAttIds = await db.NoteAttachments.AsNoTracking().Where(a => a.PageId == templatePageId).Select(a => a.AttachmentId).ToListAsync(ct);
        var srcAtts = srcAttIds.Count == 0 ? [] : await db.Attachments.AsNoTracking().Where(a => srcAttIds.Contains(a.Id) && a.DeletedAt == null).ToListAsync(ct);
        var remap = new List<(byte[] OldBytes, byte[] NewBytes)>(srcAtts.Count);
        foreach (var att in srcAtts)
        {
            var cloneId = Guid.NewGuid();
            db.Attachments.Add(new Attachment
            {
                Id = cloneId, Area = att.Area, FileName = att.FileName, ContentType = att.ContentType, Size = att.Size, Sha256 = att.Sha256,
                StoragePath = att.StoragePath, UploaderId = ownerId, CreatedAt = now,
            });
            db.NoteAttachments.Add(new NoteAttachment { AttachmentId = cloneId, PageId = copy.Id });
            remap.Add((System.Text.Encoding.ASCII.GetBytes(att.Id.ToString("D")), System.Text.Encoding.ASCII.GetBytes(cloneId.ToString("D"))));
        }
        copy.YState = RemapGuids(src.YState, remap);
        if (src.CoverImageId is { } coverImg)
        {
            var idx = srcAtts.FindIndex(a => a.Id == coverImg);
            if (idx >= 0 && Guid.TryParse(System.Text.Encoding.ASCII.GetString(remap[idx].NewBytes), out var newCover)) copy.CoverImageId = newCover;
        }
        // Edits not yet folded into the snapshot travel along (Yjs merges them on first open).
        var updates = await db.NoteUpdates.AsNoTracking().Where(u => u.PageId == templatePageId).OrderBy(u => u.Id).Select(u => u.Data).ToListAsync(ct);
        foreach (var data in updates)
            db.NoteUpdates.Add(new NoteUpdate { PageId = copy.Id, UserId = ownerId, Data = RemapGuids(data, remap)!, CreatedAt = now });
        await db.SaveChangesAsync(ct);

        // Everyone who can open the new page (it sits in the same parent as the template) except its owner.
        var audience = (await GetAudienceAsync(copy.Id, ct)).Where(u => u != ownerId).ToList();
        return Ok(new NoteRecurrenceRunDto(copy.Id, copy.Title, audience));
    }
}
