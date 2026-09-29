using IPop.Application.Notes;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Notes: pages, sharing, Yjs sync bookkeeping, versions and comments.
/// Access to a page is the highest grant on the page or any ancestor: owning any page in the chain
/// makes you owner, and a share (to you, or to a chat room you are currently in) grants its level.
/// A sub-page therefore inherits every grant of its parents and can only add more.
public sealed partial class NoteService(IPopDbContext db) : INoteService
{
    private const int MaxDepth = 32;
    private const int MaxUpdateBytes = 512 * 1024;
    private static readonly TimeSpan VersionEvery = TimeSpan.FromMinutes(10);
    public const string GuideTitle = "Mulai di sini: Panduan Notes";

    // ── Access ───────────────────────────────────────────────────────────────

    private sealed record Node(Guid Id, Guid? ParentId, Guid OwnerId, DateTime? DeletedAt);

    private Task<List<Guid>> RoomIdsAsync(Guid userId, CancellationToken ct) =>
        db.ChatRoomMembers.AsNoTracking().Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.RoomId).ToListAsync(ct);

    /// The page followed by its ancestors, nearest first.
    private async Task<List<Node>> ChainAsync(Guid pageId, CancellationToken ct)
    {
        var chain = new List<Node>();
        Guid? next = pageId;
        while (next is { } id && chain.Count < MaxDepth && chain.All(n => n.Id != id))
        {
            var n = await db.NotePages.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new Node(p.Id, p.ParentId, p.OwnerId, p.DeletedAt)).FirstOrDefaultAsync(ct);
            if (n is null) break;
            chain.Add(n);
            next = n.ParentId;
        }
        return chain;
    }

    private async Task<NoteAccess> AccessOnChainAsync(Guid userId, List<Node> chain, CancellationToken ct)
    {
        if (chain.Count == 0) return NoteAccess.None;
        if (chain.Any(n => n.OwnerId == userId)) return NoteAccess.Owner;
        var ids = chain.Select(n => n.Id).ToList();
        var rooms = await RoomIdsAsync(userId, ct);
        var levels = await db.NoteShares.AsNoTracking()
            .Where(s => ids.Contains(s.PageId) && (s.UserId == userId || (s.RoomId != null && rooms.Contains(s.RoomId.Value))))
            .Select(s => s.Level).ToListAsync(ct);
        return levels.Select(NoteLevels.ToAccess).DefaultIfEmpty(NoteAccess.None).Max();
    }

    /// Access to a live page; a page in Trash (or under a trashed parent) gives none.
    public async Task<NoteAccess> GetAccessAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var chain = await ChainAsync(pageId, ct);
        if (chain.Count == 0 || chain.Any(n => n.DeletedAt != null)) return NoteAccess.None;
        return await AccessOnChainAsync(userId, chain, ct);
    }

    private async Task<(NotePage? Page, NoteAccess Access)> LoadAsync(Guid userId, Guid pageId, NoteAccess need, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < need) return (null, access);
        var page = await db.NotePages.FirstOrDefaultAsync(p => p.Id == pageId, ct);
        return (page, access);
    }

    private static NoteResult<T> Fail<T>(string code) => new(default, code);
    private static NoteResult<T> Ok<T>(T value) => new(value, null);
    private static string Denied(NoteAccess have) => have == NoteAccess.None ? "not_found" : "forbidden";

    // ── Visible set (sidebar, search) ─────────────────────────────────────────

    private sealed record Visible(Guid Id, Guid? ParentId, Guid OwnerId, NoteAccess Access);

    /// Every live page the user can open: owned pages, pages shared to them or their rooms,
    /// and everything beneath those. Access per page is the max along its visible chain.
    private async Task<Dictionary<Guid, Visible>> VisibleAsync(Guid userId, CancellationToken ct)
    {
        var rooms = await RoomIdsAsync(userId, ct);
        var grants = await db.NoteShares.AsNoTracking()
            .Where(s => s.UserId == userId || (s.RoomId != null && rooms.Contains(s.RoomId.Value)))
            .Select(s => new { s.PageId, s.Level }).ToListAsync(ct);
        var grantByPage = grants.GroupBy(g => g.PageId)
            .ToDictionary(g => g.Key, g => g.Select(x => NoteLevels.ToAccess(x.Level)).Max());

        var seeds = grantByPage.Keys.ToList();
        var nodes = await db.NotePages.AsNoTracking()
            .Where(p => p.DeletedAt == null && (p.OwnerId == userId || seeds.Contains(p.Id)))
            .Select(p => new Node(p.Id, p.ParentId, p.OwnerId, p.DeletedAt)).ToListAsync(ct);
        var all = nodes.ToDictionary(n => n.Id);

        // Walk down: children of anything visible are visible.
        var frontier = all.Keys.ToList();
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var kids = await db.NotePages.AsNoTracking()
                .Where(p => p.DeletedAt == null && p.ParentId != null && frontier.Contains(p.ParentId.Value))
                .Select(p => new Node(p.Id, p.ParentId, p.OwnerId, p.DeletedAt)).ToListAsync(ct);
            frontier = [];
            foreach (var k in kids.Where(k => all.TryAdd(k.Id, k))) frontier.Add(k.Id);
        }

        // A page under a trashed (or otherwise hidden-to-everyone) parent is not live.
        var parentIds = all.Values.Where(n => n.ParentId is { } p && !all.ContainsKey(p)).Select(n => n.ParentId!.Value).Distinct().ToList();
        var deadParents = parentIds.Count == 0 ? [] : (await db.NotePages.AsNoTracking()
            .Where(p => parentIds.Contains(p.Id) && p.DeletedAt != null).Select(p => p.Id).ToListAsync(ct)).ToHashSet();

        var result = new Dictionary<Guid, Visible>();
        NoteAccess Resolve(Guid id, int guard)
        {
            if (result.TryGetValue(id, out var done)) return done.Access;
            var n = all[id];
            var own = n.OwnerId == userId ? NoteAccess.Owner : grantByPage.GetValueOrDefault(id, NoteAccess.None);
            var inherited = n.ParentId is { } p && all.ContainsKey(p) && guard < MaxDepth ? Resolve(p, guard + 1) : NoteAccess.None;
            var access = own > inherited ? own : inherited;
            result[id] = new Visible(id, n.ParentId is { } pp && all.ContainsKey(pp) ? pp : null, n.OwnerId, access);
            return access;
        }
        foreach (var id in all.Keys) Resolve(id, 0);

        // Drop subtrees whose invisible parent is in Trash.
        bool Dead(Guid id, int guard)
        {
            var n = all[id];
            if (n.ParentId is not { } p) return false;
            if (all.ContainsKey(p)) return guard < MaxDepth && Dead(p, guard + 1);
            return deadParents.Contains(p);
        }
        foreach (var id in all.Keys.Where(id => Dead(id, 0)).ToList()) result.Remove(id);
        return result.Where(kv => kv.Value.Access > NoteAccess.None).ToDictionary();
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    public async Task<Guid> EnsureGuideAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.NotePages.AsNoTracking().Where(p => p.OwnerId == userId && p.IsGuide)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;
        var now = DateTime.UtcNow;
        var page = new NotePage
        {
            Id = Guid.NewGuid(), OwnerId = userId, Title = GuideTitle, Emoji = "👋", Cover = "mesh",
            Accent = "orange", Font = "default", IsGuide = true, SeedTemplate = "guide", TodoTotal = 13,
            CreatedAt = now, UpdatedAt = now,
        };
        db.NotePages.Add(page);
        await db.SaveChangesAsync(ct);
        return page.Id;
    }

    public async Task<NoteSidebarDto> GetSidebarAsync(Guid userId, CancellationToken ct)
    {
        var visible = await VisibleAsync(userId, ct);
        var ids = visible.Keys.ToList();
        var rows = await db.NotePages.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Title, p.Emoji, p.IsGuide, p.TodoDone, p.TodoTotal, p.UpdatedAt, OwnerName = p.Owner.Name })
            .ToListAsync(ct);
        var favs = (await db.NoteFavorites.AsNoTracking().Where(f => f.UserId == userId).Select(f => f.PageId).ToListAsync(ct)).ToHashSet();
        var sharedIds = (await db.NoteShares.AsNoTracking().Where(s => ids.Contains(s.PageId)).Select(s => s.PageId).Distinct().ToListAsync(ct)).ToHashSet();

        bool IsShared(Guid id)
        {
            for (Guid? cur = id; cur is { } c && visible.TryGetValue(c, out var v); cur = v.ParentId)
                if (sharedIds.Contains(c)) return true;
            return false;
        }

        var pages = rows.Select(r =>
        {
            var v = visible[r.Id];
            return new NoteListItemDto(r.Id, v.ParentId, r.Title, r.Emoji, NoteLevels.Name(v.Access),
                v.OwnerId == userId, IsShared(r.Id), favs.Contains(r.Id), r.IsGuide && v.OwnerId == userId,
                r.TodoDone, r.TodoTotal, r.UpdatedAt, r.OwnerName);
        }).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var recent = await db.NoteVisits.AsNoTracking().Where(v => v.UserId == userId && ids.Contains(v.PageId))
            .OrderByDescending(v => v.VisitedAt).Take(8).Select(v => v.PageId).ToListAsync(ct);
        return new NoteSidebarDto(pages, recent);
    }

    public async Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(Guid userId, string query, CancellationToken ct)
    {
        var q = query.Trim().ToLower();
        if (q.Length == 0) return [];
        var ids = (await VisibleAsync(userId, ct)).Keys.ToList();
        var rows = await db.NotePages.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && (p.Title.ToLower().Contains(q) || (p.PlainText != null && p.PlainText.ToLower().Contains(q))))
            .OrderByDescending(p => p.UpdatedAt).Take(30)
            .Select(p => new { p.Id, p.Title, p.Emoji, p.PlainText }).ToListAsync(ct);
        return rows.Select(r => new NoteSearchHitDto(r.Id, r.Title, r.Emoji, Snippet(r.PlainText, q))).ToList();
    }

    private static string? Snippet(string? text, string q)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return text.Length <= 120 ? text : text[..120] + "…";
        var start = Math.Max(0, i - 40);
        var len = Math.Min(text.Length - start, 120);
        return (start > 0 ? "…" : "") + text.Substring(start, len) + (start + len < text.Length ? "…" : "");
    }

    public async Task<IReadOnlyList<NoteListItemDto>> GetTrashAsync(Guid userId, CancellationToken ct)
    {
        // Top of each trashed subtree the user owns or trashed themselves.
        var rows = await db.NotePages.AsNoTracking()
            .Where(p => p.DeletedAt != null && (p.OwnerId == userId || p.DeletedById == userId)
                && (p.ParentId == null || p.Parent!.DeletedAt == null || p.Parent.DeletedAt != p.DeletedAt))
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new NoteListItemDto(p.Id, null, p.Title, p.Emoji, "owner", p.OwnerId == userId, false, false,
                p.IsGuide, p.TodoDone, p.TodoTotal, p.DeletedAt!.Value, p.Owner.Name))
            .ToListAsync(ct);
        return rows;
    }

    // ── Page ──────────────────────────────────────────────────────────────────

    public async Task<NoteResult<NotePageDto>> GetPageAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var chain = await ChainAsync(pageId, ct);
        if (chain.Count == 0 || chain.Any(n => n.DeletedAt != null)) return Fail<NotePageDto>("not_found");
        var access = await AccessOnChainAsync(userId, chain, ct);
        if (access == NoteAccess.None) return Fail<NotePageDto>("not_found");

        var visit = await db.NoteVisits.FirstOrDefaultAsync(v => v.UserId == userId && v.PageId == pageId, ct);
        if (visit is null) db.NoteVisits.Add(new NoteVisit { UserId = userId, PageId = pageId, VisitedAt = DateTime.UtcNow });
        else visit.VisitedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two tabs opening the same page race on the insert; the other one already recorded the visit.
            db.ChangeTracker.Clear();
        }

        return Ok(await BuildPageAsync(userId, pageId, chain, access, ct));
    }

    private async Task<NotePageDto> BuildPageAsync(Guid userId, Guid pageId, List<Node> chain, NoteAccess access, CancellationToken ct)
    {
        var p = await db.NotePages.AsNoTracking().Where(x => x.Id == pageId).Select(x => new
        {
            x.Id, x.ParentId, x.Title, x.Emoji, x.Cover, x.Accent, x.Font, x.FullWidth, x.SmallText, x.IsGuide,
            x.CreatedAt, x.UpdatedAt, x.UpdatedById, x.TodoDone, x.TodoTotal,
            OwnerName = x.Owner.Name, OwnerNik = x.Owner.Nik,
        }).FirstAsync(ct);

        // Breadcrumb: ancestors the user can open (all of them unless access came from a sub-page share).
        var crumbs = new List<NoteCrumbDto>();
        for (var i = 1; i < chain.Count; i++)
        {
            var sub = chain.Skip(i).ToList();
            if (await AccessOnChainAsync(userId, sub, ct) == NoteAccess.None) break;
            var a = await db.NotePages.AsNoTracking().Where(x => x.Id == chain[i].Id)
                .Select(x => new NoteCrumbDto(x.Id, x.Title, x.Emoji)).FirstAsync(ct);
            crumbs.Insert(0, a);
        }
        var children = await db.NotePages.AsNoTracking().Where(x => x.ParentId == pageId && x.DeletedAt == null)
            .OrderBy(x => x.CreatedAt).Select(x => new NoteCrumbDto(x.Id, x.Title, x.Emoji)).ToListAsync(ct);

        var chainIds = chain.Select(n => n.Id).ToList();
        var roomShares = await db.NoteShares.AsNoTracking().Where(s => chainIds.Contains(s.PageId) && s.RoomId != null)
            .Select(s => new { RoomId = s.RoomId!.Value, s.Level }).ToListAsync(ct);
        var rooms = new List<NoteRoomShareDto>();
        foreach (var g in roomShares.GroupBy(r => r.RoomId))
            rooms.Add(new NoteRoomShareDto(g.Key, await RoomLabelAsync(g.Key, userId, ct),
                NoteLevels.Name(g.Select(x => NoteLevels.ToAccess(x.Level)).Max())));
        var isShared = rooms.Count > 0 || await db.NoteShares.AnyAsync(s => chainIds.Contains(s.PageId), ct);

        var updatedBy = p.UpdatedById is { } ub ? await db.Users.AsNoTracking().Where(u => u.Id == ub).Select(u => u.Name).FirstOrDefaultAsync(ct) : null;
        var fav = await db.NoteFavorites.AnyAsync(f => f.UserId == userId && f.PageId == pageId, ct);
        var open = await db.NoteThreads.CountAsync(t => t.PageId == pageId && t.ResolvedAt == null, ct);

        return new NotePageDto(p.Id, p.ParentId, p.Title, p.Emoji, p.Cover, p.Accent, p.Font, p.FullWidth, p.SmallText,
            p.IsGuide, NoteLevels.Name(access), fav, isShared, p.OwnerName, p.OwnerNik, p.CreatedAt, p.UpdatedAt, updatedBy,
            p.TodoDone, p.TodoTotal, open, crumbs, children, rooms);
    }

    /// Group name, or for a DM the other person's name as seen by <paramref name="viewerId"/>.
    private async Task<string> RoomLabelAsync(Guid roomId, Guid viewerId, CancellationToken ct)
    {
        var room = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId).Select(r => new { r.Type, r.Name }).FirstOrDefaultAsync(ct);
        if (room is null) return "Chat";
        if (room.Type == "group") return room.Name ?? "Group";
        var names = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId)
            .OrderBy(m => m.UserId == viewerId).Select(m => m.User.Name).ToListAsync(ct);
        return names.Count == 0 ? "Direct message" : names.Count == 1 ? names[0] : string.Join(" & ", names.Take(2));
    }

    public async Task<NoteResult<NotePageDto>> CreateAsync(Guid userId, CreateNoteRequest req, CancellationToken ct)
    {
        string accent = "blue", font = "default";
        string? cover = null;
        if (req.ParentId is { } parentId)
        {
            var access = await GetAccessAsync(userId, parentId, ct);
            if (access < NoteAccess.Edit) return Fail<NotePageDto>(Denied(access));
        }
        if (req.Template is { } t && (!NoteStyle.Templates.Contains(t) || t == "guide")) return Fail<NotePageDto>("invalid_template");
        if (req.Accent is { } a) { if (!NoteStyle.Accents.Contains(a)) return Fail<NotePageDto>("invalid_style"); accent = a; }
        if (req.Cover is { } c) { if (!NoteStyle.Covers.Contains(c)) return Fail<NotePageDto>("invalid_style"); cover = c; }

        var now = DateTime.UtcNow;
        var page = new NotePage
        {
            Id = Guid.NewGuid(), OwnerId = userId, ParentId = req.ParentId,
            Title = Clean(req.Title, 200) ?? "", Emoji = Clean(req.Emoji, 16),
            Cover = cover, Accent = accent, Font = font, SeedTemplate = req.Template,
            CreatedAt = now, UpdatedAt = now, UpdatedById = userId,
        };
        db.NotePages.Add(page);
        await db.SaveChangesAsync(ct);
        return await GetPageAsync(userId, page.Id, ct);
    }

    private static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s.Length <= max ? s : s[..max];
    }

    public async Task<NoteResult<NotePageDto>> UpdateMetaAsync(Guid userId, Guid pageId, UpdateNoteMetaRequest req, CancellationToken ct)
    {
        var (page, access) = await LoadAsync(userId, pageId, NoteAccess.Edit, ct);
        if (page is null) return Fail<NotePageDto>(Denied(access));
        if (req.Cover is { } c && c != "" && !NoteStyle.Covers.Contains(c)) return Fail<NotePageDto>("invalid_style");
        if (req.Accent is { } a && !NoteStyle.Accents.Contains(a)) return Fail<NotePageDto>("invalid_style");
        if (req.Font is { } f && !NoteStyle.Fonts.Contains(f)) return Fail<NotePageDto>("invalid_style");

        if (req.Title is not null) page.Title = Clean(req.Title, 200) ?? "";
        if (req.Emoji is not null) page.Emoji = Clean(req.Emoji, 16);
        if (req.Cover is not null) page.Cover = req.Cover == "" ? null : req.Cover;
        if (req.Accent is not null) page.Accent = req.Accent;
        if (req.Font is not null) page.Font = req.Font;
        if (req.FullWidth is { } w) page.FullWidth = w;
        if (req.SmallText is { } s) page.SmallText = s;
        page.UpdatedAt = DateTime.UtcNow;
        page.UpdatedById = userId;
        await db.SaveChangesAsync(ct);
        return Ok(await BuildPageAsync(userId, pageId, await ChainAsync(pageId, ct), access, ct));
    }

    public async Task<NoteResult<bool>> SetFavoriteAsync(Guid userId, Guid pageId, bool favorite, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<bool>("not_found");
        var row = await db.NoteFavorites.FirstOrDefaultAsync(f => f.UserId == userId && f.PageId == pageId, ct);
        if (favorite && row is null) db.NoteFavorites.Add(new NoteFavorite { UserId = userId, PageId = pageId, CreatedAt = DateTime.UtcNow });
        if (!favorite && row is not null) db.NoteFavorites.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(true);
    }

    private async Task<List<Guid>> SubtreeAsync(Guid pageId, Func<IQueryable<NotePage>, IQueryable<NotePage>> filter, CancellationToken ct)
    {
        var ids = new List<Guid> { pageId };
        var frontier = new List<Guid> { pageId };
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var f = frontier;
            frontier = await filter(db.NotePages.Where(p => p.ParentId != null && f.Contains(p.ParentId.Value))).Select(p => p.Id).ToListAsync(ct);
            frontier = frontier.Except(ids).ToList();
            ids.AddRange(frontier);
        }
        return ids;
    }

    /// Editors may move a page (and its sub-pages) to Trash; it can be restored by its owner or by whoever trashed it.
    public async Task<NoteResult<bool>> TrashAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<bool>(Denied(access));
        var ids = await SubtreeAsync(pageId, q => q.Where(p => p.DeletedAt == null), ct);
        var now = DateTime.UtcNow;
        await db.NotePages.Where(p => ids.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, now).SetProperty(p => p.DeletedById, userId), ct);
        return Ok(true);
    }

    private async Task<NotePage?> TrashedTopAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var page = await db.NotePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.DeletedAt != null, ct);
        if (page is null) return null;
        if (page.OwnerId == userId || page.DeletedById == userId) return page;
        return await AccessOnChainAsync(userId, await ChainAsync(pageId, ct), ct) == NoteAccess.Owner ? page : null;
    }

    public async Task<NoteResult<bool>> RestoreAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var page = await TrashedTopAsync(userId, pageId, ct);
        if (page is null) return Fail<bool>("not_found");
        var at = page.DeletedAt;
        var ids = await SubtreeAsync(pageId, q => q.Where(p => p.DeletedAt == at), ct);
        // Restoring under a parent that is itself in Trash would leave it unreachable: lift it to the top level.
        var parentGone = page.ParentId is { } pid && await db.NotePages.AnyAsync(p => p.Id == pid && p.DeletedAt != null, ct);
        await db.NotePages.Where(p => ids.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, (DateTime?)null).SetProperty(p => p.DeletedById, (Guid?)null), ct);
        if (parentGone)
            await db.NotePages.Where(p => p.Id == pageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ParentId, (Guid?)null), ct);
        return Ok(true);
    }

    public async Task<NoteResult<bool>> PurgeAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var page = await TrashedTopAsync(userId, pageId, ct);
        if (page is null) return Fail<bool>("not_found");
        var ids = await SubtreeAsync(pageId, q => q, ct);
        ids.Reverse();   // children first, so the self-referencing FK never blocks
        foreach (var id in ids)
            await db.NotePages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return Ok(true);
    }
}
