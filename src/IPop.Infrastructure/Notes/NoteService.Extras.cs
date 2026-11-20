using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Named versions, the owner's own templates, and the "who opened this page" list.
public sealed partial class NoteService
{
    private const int MaxNamedVersions = 50;
    private const int MaxTemplatesPerUser = 30;

    // ── Named versions ───────────────────────────────────────────────────────

    public async Task<NoteResult<NoteVersionDto>> NameVersionAsync(Guid userId, Guid pageId, Guid versionId, string? name, CancellationToken ct)
    {
        var (page, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (page is null) return Fail<NoteVersionDto>(Denied(access));
        if (page.LockedAt is not null) return Fail<NoteVersionDto>("locked");
        var v = await db.NoteVersions.FirstOrDefaultAsync(x => x.Id == versionId && x.PageId == pageId, ct);
        if (v is null) return Fail<NoteVersionDto>("not_found");
        var clean = Clean(name, 100);
        if (clean is not null && v.Name is null
            && await db.NoteVersions.CountAsync(x => x.PageId == pageId && x.Name != null, ct) >= MaxNamedVersions) return Fail<NoteVersionDto>("too_many_named");
        v.Name = clean;
        v.NamedById = clean is null ? null : userId;
        await db.SaveChangesAsync(ct);
        return Ok(new NoteVersionDto(v.Id, v.CreatedAt, v.Title, [], v.Name));
    }

    public async Task<NoteResult<NoteVersionDto>> SaveNamedVersionAsync(Guid userId, Guid pageId, string? name, CancellationToken ct)
    {
        var (page, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (page is null) return Fail<NoteVersionDto>(Denied(access));
        if (page.LockedAt is not null) return Fail<NoteVersionDto>("locked");
        var clean = Clean(name, 100);
        if (clean is null) return Fail<NoteVersionDto>("name_required");
        if (page.YState is null) return Fail<NoteVersionDto>("empty_page");
        if (await db.NoteVersions.CountAsync(x => x.PageId == pageId && x.Name != null, ct) >= MaxNamedVersions) return Fail<NoteVersionDto>("too_many_named");
        var v = new NoteVersion
        {
            Id = Guid.NewGuid(), PageId = pageId, State = page.YState, Title = page.Title, Name = clean, NamedById = userId,
            Contributors = page.PendingContributors ?? userId.ToString(), CreatedAt = DateTime.UtcNow,
        };
        db.NoteVersions.Add(v);
        if (!await db.NoteUpdates.AsNoTracking().AnyAsync(u => u.PageId == pageId, ct))
            page.PendingContributors = null;
        await db.SaveChangesAsync(ct);
        return Ok(new NoteVersionDto(v.Id, v.CreatedAt, v.Title, [], v.Name));
    }

    // ── Templates ────────────────────────────────────────────────────────────

    private static NoteTemplateDto Dto(NoteTemplate t) => new(t.Id, t.Name, t.Emoji, t.Accent, t.Kind, t.Excerpt, t.CreatedAt);

    public async Task<NoteResult<NoteTemplateDto>> SaveTemplateAsync(Guid userId, Guid pageId, string? name, CancellationToken ct)
    {
        // A template is a durable copy of the page, so it is limited to people who can edit it (not just read it).
        var (page, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (page is null) return Fail<NoteTemplateDto>(Denied(access));
        if (page.YState is null) return Fail<NoteTemplateDto>("empty_page");
        if (await db.NoteTemplates.CountAsync(t => t.OwnerId == userId, ct) >= MaxTemplatesPerUser) return Fail<NoteTemplateDto>("too_many_templates");
        var title = Clean(page.Title, 100) ?? "Untitled";
        var t = new NoteTemplate
        {
            Id = Guid.NewGuid(), OwnerId = userId, Name = Clean(name, 100) ?? title, Emoji = page.Emoji, Accent = page.Accent, Kind = page.Kind,
            State = page.YState, Excerpt = page.Excerpt, SourcePageId = pageId, CreatedAt = DateTime.UtcNow,
        };
        db.NoteTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return Ok(Dto(t));
    }

    public async Task<IReadOnlyList<NoteTemplateDto>> GetTemplatesAsync(Guid userId, CancellationToken ct) =>
        (await db.NoteTemplates.AsNoTracking().Where(t => t.OwnerId == userId).OrderByDescending(t => t.CreatedAt).Take(MaxTemplatesPerUser).ToListAsync(ct))
        .Select(Dto).ToList();

    public async Task<NoteResult<bool>> DeleteTemplateAsync(Guid userId, Guid templateId, CancellationToken ct)
    {
        var n = await db.NoteTemplates.Where(t => t.Id == templateId && t.OwnerId == userId).ExecuteDeleteAsync(ct);
        return n == 1 ? Ok(true) : Fail<bool>("not_found");
    }

    public async Task<NoteResult<NotePageDto>> CreateFromTemplateAsync(Guid userId, Guid templateId, NewFromTemplateRequest req, CancellationToken ct)
    {
        var t = await db.NoteTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == templateId && x.OwnerId == userId, ct);
        if (t is null) return Fail<NotePageDto>("not_found");
        if (req.ParentId is { } parentId)
        {
            var chain = await ChainAsync(parentId, ct);
            if (chain.Count == 0 || chain.Any(n => n.DeletedAt != null)) return Fail<NotePageDto>("not_found");
            if (chain.Count >= MaxDepth || chain[^1].ParentId != null) return Fail<NotePageDto>("max_depth");
            var access = await AccessOnChainAsync(userId, chain, ct);
            if (access < NoteAccess.Edit) return Fail<NotePageDto>(Denied(access));
        }
        var now = DateTime.UtcNow;
        var page = new NotePage
        {
            Id = Guid.NewGuid(), OwnerId = userId, ParentId = req.ParentId,
            Title = Clean(req.Title, 200) ?? t.Name, Emoji = t.Emoji, Kind = t.Kind, Accent = t.Accent, Font = "default",
            YState = t.State, Excerpt = t.Excerpt, CreatedAt = now, UpdatedAt = now, UpdatedById = userId,
        };
        db.NotePages.Add(page);

        // Files inside the template are cloned for the new page only while the caller can still read them.
        var ids = GuidText.Matches(System.Text.Encoding.Latin1.GetString(t.State)).Select(m => Guid.Parse(m.Value)).Distinct().Take(500).ToList();
        var atts = await FilterReadableNotesAttachmentsAsync(userId, ids, ct);
        var remap = new List<(byte[] OldBytes, byte[] NewBytes)>(atts.Count);
        foreach (var att in atts)
        {
            var cloneId = Guid.NewGuid();
            db.Attachments.Add(new Attachment
            {
                Id = cloneId, Area = att.Area, FileName = att.FileName, ContentType = att.ContentType, Size = att.Size, Sha256 = att.Sha256,
                StoragePath = att.StoragePath, UploaderId = userId, CreatedAt = now,
            });
            db.NoteAttachments.Add(new NoteAttachment { AttachmentId = cloneId, PageId = page.Id });
            remap.Add((System.Text.Encoding.ASCII.GetBytes(att.Id.ToString("D")), System.Text.Encoding.ASCII.GetBytes(cloneId.ToString("D"))));
        }
        page.YState = RemapGuids(t.State, remap);
        await db.SaveChangesAsync(ct);
        return await GetPageAsync(userId, page.Id, ct);
    }

    // ── Page views ───────────────────────────────────────────────────────────

    /// One row per person per page, bumped on every open. Only the owner and editors can read the list back.
    private async Task RecordViewAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var bumped = await db.NoteViews.Where(v => v.PageId == pageId && v.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.LastViewedAt, now).SetProperty(v => v.Count, v => v.Count + 1), ct);
        if (bumped > 0) return;
        try
        {
            db.NoteViews.Add(new NoteView { PageId = pageId, UserId = userId, LastViewedAt = now, Count = 1 });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // two tabs raced the insert; the other one won
    }

    public async Task<NoteResult<IReadOnlyList<NoteViewerDto>>> GetViewersAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<IReadOnlyList<NoteViewerDto>>(Denied(access));
        var rows = await (from v in db.NoteViews.AsNoTracking()
                          join u in db.Users.AsNoTracking() on v.UserId equals u.Id
                          where v.PageId == pageId && v.UserId != userId
                          orderby v.LastViewedAt descending
                          select new NoteViewerDto(u.Id, u.Name, null, v.LastViewedAt, v.Count)).Take(50).ToListAsync(ct);
        return Ok<IReadOnlyList<NoteViewerDto>>(rows);
    }
}
