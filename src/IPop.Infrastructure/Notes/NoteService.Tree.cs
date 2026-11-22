using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Sidebar tree edits: move (re-parent), reorder among siblings, duplicate.
public sealed partial class NoteService
{
    public async Task<NoteResult<bool>> MoveAsync(Guid userId, Guid pageId, MoveNoteRequest req, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<bool>("not_found");
        var page = await db.NotePages.FirstAsync(p => p.Id == pageId, ct);
        if (page.LockedAt is not null) return Fail<bool>("locked");

        if (page.ParentId != req.ParentId)
        {
            // Re-parenting changes who inherits access, so only an owner may do it; a top-level page belongs
            // to its owner's tree, so only the page's own owner can lift it there.
            if (access < NoteAccess.Owner || (req.ParentId is null && page.OwnerId != userId)) return Fail<bool>("forbidden");
            if (req.ParentId is { } target)
            {
                var targetChain = await ChainAsync(target, ct);
                if (targetChain.Count == 0 || targetChain.Any(n => n.DeletedAt != null) || targetChain[^1].ParentId != null) return Fail<bool>("forbidden");
                if (targetChain.Any(n => n.Id == pageId)) return Fail<bool>("invalid_move");
                var targetAccess = await AccessOnChainAsync(userId, targetChain, ct);
                if (targetAccess < NoteAccess.Edit) return Fail<bool>("forbidden");
                var height = await SubtreeHeightAsync(pageId, ct);
                if (targetChain.Count + height > MaxDepth) return Fail<bool>("max_depth");
            }
            if (!req.Confirm && await AudienceChangesAsync(page, req.ParentId, ct)) return Fail<bool>("confirm_audience");
        }
        else if (req.ParentId is { } parent)
        {
            if (await GetAccessAsync(userId, parent, ct) < NoteAccess.Edit) return Fail<bool>("forbidden");
        }
        else if (page.OwnerId != userId) return Fail<bool>("forbidden");   // shared roots stay alphabetical

        // Siblings in their current order; a top-level list is the owner's own.
        var siblings = await db.NotePages
            .Where(p => p.DeletedAt == null && p.Id != pageId && p.ParentId == req.ParentId
                && (req.ParentId != null || p.OwnerId == page.OwnerId))
            .ToListAsync(ct);
        var ordered = Arrange(siblings, req.ParentId is null);
        var at = req.BeforeId is { } before ? ordered.FindIndex(p => p.Id == before) : -1;
        ordered.Insert(at < 0 ? ordered.Count : at, page);
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i + 1;

        page.ParentId = req.ParentId;
        await db.SaveChangesAsync(ct);
        return Ok(true);
    }

    /// Default sibling order, mirrored by the client: placed pages by SortOrder; unplaced sub-pages after them
    /// by title, unplaced top-level pages before them, newest first.
    private static List<NotePage> Arrange(List<NotePage> pages, bool top)
    {
        var placed = pages.Where(p => p.SortOrder > 0).OrderBy(p => p.SortOrder);
        var rest = pages.Where(p => p.SortOrder <= 0);
        return top
            ? rest.OrderByDescending(p => p.UpdatedAt).Concat(placed).ToList()
            : placed.Concat(rest.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    /// Whether moving the page under <paramref name="newParent"/> changes who inherits access or their access level.
    private async Task<bool> AudienceChangesAsync(NotePage page, Guid? newParent, CancellationToken ct)
    {
        async Task<Dictionary<Guid, NoteAccess>> Inherited(Guid? parent)
        {
            var map = new Dictionary<Guid, NoteAccess> { [page.OwnerId] = NoteAccess.Owner };
            if (parent is not { } pid) return map;
            var chain = await ChainAsync(pid, ct);
            if (chain.Count == 0) return map;
            foreach (var n in chain) map[n.OwnerId] = NoteAccess.Owner;
            var chainIds = chain.Select(n => n.Id).ToList();
            var shares = await db.NoteShares.AsNoTracking().Where(s => chainIds.Contains(s.PageId))
                .Select(s => new { s.UserId, s.RoomId, s.Level }).ToListAsync(ct);
            void Grant(Guid uid, NoteAccess lvl)
            {
                if (!map.TryGetValue(uid, out var prev) || lvl > prev) map[uid] = lvl;
            }
            foreach (var s in shares.Where(s => s.UserId != null)) Grant(s.UserId!.Value, NoteLevels.ToAccess(s.Level));
            var rooms = shares.Where(s => s.RoomId != null).Select(s => s.RoomId!.Value).Distinct().ToList();
            if (rooms.Count > 0)
            {
                var members = await db.ChatRoomMembers.AsNoTracking().Where(m => rooms.Contains(m.RoomId) && m.LeftAt == null)
                    .Select(m => new { m.RoomId, m.UserId }).ToListAsync(ct);
                foreach (var s in shares.Where(s => s.RoomId != null))
                {
                    var lvl = NoteLevels.ToAccess(s.Level);
                    foreach (var m in members.Where(m => m.RoomId == s.RoomId)) Grant(m.UserId, lvl);
                }
            }
            return map;
        }
        var before = await Inherited(page.ParentId);
        var after = await Inherited(newParent);
        return before.Count != after.Count || before.Any(kv => !after.TryGetValue(kv.Key, out var lvl) || lvl != kv.Value);
    }

    /// Copies the page alone (no sub-pages, no shares): content, icon, cover and style, as "Copy of …".
    /// It lands next to the original when the caller can edit there, otherwise at the top of their pages.
    public async Task<NoteResult<NotePageDto>> DuplicateAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var (src, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (src is null) return Fail<NotePageDto>(Denied(access));
        var parent = src.ParentId is { } pid && await GetAccessAsync(userId, pid, ct) >= NoteAccess.Edit ? src.ParentId : null;
        if (parent is { } parentId && (await ChainAsync(parentId, ct)).Count >= MaxDepth) parent = null;

        var now = DateTime.UtcNow;
        var title = string.IsNullOrWhiteSpace(src.Title) ? "Untitled" : src.Title;
        var copy = new NotePage
        {
            Id = Guid.NewGuid(), OwnerId = userId, ParentId = parent,
            Title = Clean("Copy of " + title, 200)!, Emoji = src.Emoji, Kind = src.Kind, Cover = src.Cover, CoverPosY = src.CoverPosY, Accent = src.Accent,
            Font = src.Font, FullWidth = src.FullWidth, SmallText = src.SmallText, SeedTemplate = src.SeedTemplate == "guide" ? null : src.SeedTemplate,
            YState = src.YState, Excerpt = src.Excerpt, PlainText = src.PlainText,
            TodoDone = src.TodoDone, TodoTotal = src.TodoTotal,
            CreatedAt = now, UpdatedAt = now, UpdatedById = userId,
        };
        db.NotePages.Add(copy);

        // Clone attachment links so images/files in the duplicate keep working even if the original is unshared or purged (exclude board thumbnail).
        var srcAttIds = await db.NoteAttachments.AsNoTracking()
            .Where(a => a.PageId == pageId && (src.ThumbnailId == null || a.AttachmentId != src.ThumbnailId)
                && !NoteFilePreviewService.DerivedIds(db).Contains(a.AttachmentId))   // file previews are made again for the copy
            .Select(a => a.AttachmentId).ToListAsync(ct);
        var srcAtts = srcAttIds.Count == 0 ? []
            : await db.Attachments.AsNoTracking().Where(a => srcAttIds.Contains(a.Id) && a.DeletedAt == null).ToListAsync(ct);
        var remap = new List<(byte[] OldBytes, byte[] NewBytes)>(srcAtts.Count);
        foreach (var att in srcAtts)
        {
            var cloneId = Guid.NewGuid();
            db.Attachments.Add(new Attachment
            {
                Id = cloneId, Area = att.Area, FileName = att.FileName, ContentType = att.ContentType,
                Size = att.Size, Sha256 = att.Sha256, StoragePath = att.StoragePath,
                UploaderId = userId, CreatedAt = now,
            });
            db.NoteAttachments.Add(new NoteAttachment { AttachmentId = cloneId, PageId = copy.Id });
            remap.Add((System.Text.Encoding.ASCII.GetBytes(att.Id.ToString("D")),
                       System.Text.Encoding.ASCII.GetBytes(cloneId.ToString("D"))));
        }
        copy.YState = RemapGuids(src.YState, remap);
        // The photo cover is one of the cloned attachments: point the copy at its clone.
        if (src.CoverImageId is { } coverImg)
        {
            var idx = srcAtts.FindIndex(a => a.Id == coverImg);
            if (idx >= 0 && Guid.TryParse(System.Text.Encoding.ASCII.GetString(remap[idx].NewBytes), out var newCover)) copy.CoverImageId = newCover;
        }

        // Edits not yet folded into the snapshot travel along; Yjs merges them on first open.
        var updates = await db.NoteUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Id)
            .Select(u => u.Data).ToListAsync(ct);
        foreach (var data in updates)
            db.NoteUpdates.Add(new NoteUpdate { PageId = copy.Id, UserId = userId, Data = RemapGuids(data, remap)!, CreatedAt = now });

        // Right after the original among placed siblings.
        if (parent == src.ParentId && src.SortOrder > 0 && (parent != null || src.OwnerId == userId))
        {
            await db.NotePages.Where(p => p.ParentId == parent && p.SortOrder > src.SortOrder
                    && (parent != null || p.OwnerId == userId))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.SortOrder, p => p.SortOrder + 1), ct);
            copy.SortOrder = src.SortOrder + 1;
        }
        await db.SaveChangesAsync(ct);
        return await GetPageAsync(userId, copy.Id, ct);
    }

    private static byte[]? RemapGuids(byte[]? data, List<(byte[] OldBytes, byte[] NewBytes)> remap)
    {
        if (data is null || data.Length < 36 || remap.Count == 0) return data;
        byte[]? copy = null;
        var span = data.AsSpan();
        foreach (var (oldB, newB) in remap)
        {
            var pos = 0;
            while (pos <= span.Length - 36)
            {
                var idx = span[pos..].IndexOf(oldB);
                if (idx < 0) break;
                copy ??= (byte[])data.Clone();
                newB.CopyTo(copy.AsSpan(pos + idx, 36));
                span = copy.AsSpan();
                pos += idx + 36;
            }
        }
        return copy ?? data;
    }
}
