using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

public sealed partial class NoteService
{
    // ── Document sync ────────────────────────────────────────────────────────

    public async Task<NoteResult<NoteDocDto>> GetDocAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<NoteDocDto>("not_found");
        var page = await db.NotePages.AsNoTracking().Where(p => p.Id == pageId)
            .Select(p => new { p.YState, p.StateVersion }).FirstAsync(ct);
        var updates = await db.NoteUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Data }).ToListAsync(ct);
        return Ok(new NoteDocDto(page.YState is null ? null : Convert.ToBase64String(page.YState), page.StateVersion,
            updates.Select(u => new NoteUpdateDto(u.Id, Convert.ToBase64String(u.Data))).ToList()));
    }

    public async Task<NoteResult<long>> AppendUpdateAsync(Guid userId, Guid pageId, byte[] update, CancellationToken ct)
    {
        if (update.Length == 0 || update.Length > MaxUpdateBytes) return Fail<long>("invalid_update");
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<long>(Denied(access));

        var row = new NoteUpdate { PageId = pageId, UserId = userId, Data = update, CreatedAt = DateTime.UtcNow };
        db.NoteUpdates.Add(row);
        var page = await db.NotePages.FirstAsync(p => p.Id == pageId, ct);
        page.UpdatedAt = row.CreatedAt;
        page.UpdatedById = userId;
        page.PendingContributors = AddContributor(page.PendingContributors, userId);
        await db.SaveChangesAsync(ct);
        return Ok(row.Id);
    }

    private static string AddContributor(string? list, Guid userId)
    {
        var ids = (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var id = userId.ToString();
        if (!ids.Contains(id)) ids.Add(id);
        return string.Join(',', ids.TakeLast(40));
    }

    /// Folds updates into the stored state. Refused when another snapshot landed first (the client
    /// reloads and retries), so a client can only ever delete updates it has actually merged.
    public async Task<NoteResult<NoteSnapshotResultDto>> SnapshotAsync(Guid userId, Guid pageId, NoteSnapshotRequest req,
        CancellationToken ct, DateTime? now = null)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<NoteSnapshotResultDto>(Denied(access));
        byte[] state;
        try { state = Convert.FromBase64String(req.State); }
        catch (FormatException) { return Fail<NoteSnapshotResultDto>("invalid_update"); }
        if (state.Length == 0 || state.Length > 16 * 1024 * 1024) return Fail<NoteSnapshotResultDto>("invalid_update");

        var at = now ?? DateTime.UtcNow;
        var next = req.BaseVersion + 1;
        // Compare-and-swap on the version: only one snapshot per base version can win.
        var won = await db.NotePages.Where(p => p.Id == pageId && p.StateVersion == req.BaseVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StateVersion, next), ct);
        if (won == 0) return Fail<NoteSnapshotResultDto>("version_conflict");

        var page = await db.NotePages.FirstAsync(p => p.Id == pageId, ct);
        await db.Entry(page).ReloadAsync(ct);
        page.YState = state;
        var text = req.PlainText ?? "";
        page.PlainText = text.Length > 20000 ? text[..20000] : text;
        var flat = string.Join(' ', text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        page.Excerpt = flat.Length > 300 ? flat[..299] + "…" : flat;
        page.TodoDone = Math.Clamp(req.TodoDone, 0, 10000);
        page.TodoTotal = Math.Clamp(req.TodoTotal, 0, 10000);

        var merged = req.MergedUpdateIds.Distinct().ToList();
        if (merged.Count > 0)
            await db.NoteUpdates.Where(u => u.PageId == pageId && merged.Contains(u.Id)).ExecuteDeleteAsync(ct);

        var last = await db.NoteVersions.Where(v => v.PageId == pageId).MaxAsync(v => (DateTime?)v.CreatedAt, ct);
        if (page.PendingContributors is not null && (last is null || at - last.Value >= VersionEvery))
        {
            db.NoteVersions.Add(new NoteVersion
            {
                Id = Guid.NewGuid(), PageId = pageId, State = state, Title = page.Title,
                Contributors = page.PendingContributors, CreatedAt = at,
            });
            page.PendingContributors = null;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new NoteSnapshotResultDto(next));
    }

    public async Task<NoteResult<string?>> ClaimSeedAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<string?>(Denied(access));
        var template = await db.NotePages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.SeedTemplate).FirstAsync(ct);
        if (template is null) return Ok<string?>(null);
        var won = await db.NotePages.Where(p => p.Id == pageId && p.SeedTemplate == template)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SeedTemplate, (string?)null), ct);
        return Ok<string?>(won == 1 ? template : null);
    }

    // ── Versions ─────────────────────────────────────────────────────────────

    public async Task<NoteResult<IReadOnlyList<NoteVersionDto>>> GetVersionsAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<IReadOnlyList<NoteVersionDto>>("not_found");
        var rows = await db.NoteVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderByDescending(v => v.CreatedAt)
            .Take(50).Select(v => new { v.Id, v.CreatedAt, v.Title, v.Contributors }).ToListAsync(ct);
        var ids = rows.SelectMany(r => ParseIds(r.Contributors)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return Ok<IReadOnlyList<NoteVersionDto>>(rows.Select(r => new NoteVersionDto(r.Id, r.CreatedAt, r.Title,
            ParseIds(r.Contributors).Where(names.ContainsKey).Select(id => names[id]).ToList())).ToList());
    }

    private static IEnumerable<Guid> ParseIds(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty);

    public async Task<NoteResult<string>> GetVersionStateAsync(Guid userId, Guid pageId, Guid versionId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<string>("not_found");
        var state = await db.NoteVersions.AsNoTracking().Where(v => v.Id == versionId && v.PageId == pageId)
            .Select(v => v.State).FirstOrDefaultAsync(ct);
        return state is null ? Fail<string>("not_found") : Ok(Convert.ToBase64String(state));
    }

    // ── Sharing ──────────────────────────────────────────────────────────────

    public async Task<NoteResult<NoteSharesDto>> GetSharesAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var chain = await ChainAsync(pageId, ct);
        if (chain.Count == 0 || chain.Any(n => n.DeletedAt != null)) return Fail<NoteSharesDto>("not_found");
        var access = await AccessOnChainAsync(userId, chain, ct);
        if (access == NoteAccess.None) return Fail<NoteSharesDto>("not_found");

        var owner = await db.NotePages.AsNoTracking().Where(p => p.Id == pageId)
            .Select(p => new { p.OwnerId, p.Owner.Name, p.Owner.Nik, p.Owner.Position }).FirstAsync(ct);
        var ids = chain.Select(n => n.Id).ToList();
        var rows = await db.NoteShares.AsNoTracking().Where(s => ids.Contains(s.PageId)).OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id, s.PageId, s.UserId, UserName = s.User!.Name, UserNik = s.User.Nik, UserPosition = s.User.Position,
                s.RoomId, RoomType = s.Room!.Type, s.Level, PageTitle = s.Page.Title,
            }).ToListAsync(ct);

        var shares = new List<NoteShareDto>();
        foreach (var r in rows)
        {
            string? roomName = null;
            var members = 0;
            if (r.RoomId is { } rid)
            {
                roomName = await RoomLabelAsync(rid, userId, ct);
                members = await db.ChatRoomMembers.CountAsync(m => m.RoomId == rid && m.LeftAt == null, ct);
            }
            var inherited = r.PageId != pageId;
            shares.Add(new NoteShareDto(r.Id, r.UserId, r.UserId is null ? null : r.UserName, r.UserId is null ? null : r.UserNik,
                r.UserId is null ? null : r.UserPosition, r.RoomId, roomName, r.RoomId is null ? null : r.RoomType, members,
                r.Level, inherited ? r.PageId : null, inherited ? r.PageTitle : null));
        }
        return Ok(new NoteSharesDto(owner.OwnerId, owner.Name, owner.Nik, owner.Position, shares));
    }

    /// Only owners share. A room share also requires the sharer to be in that room, so nobody can
    /// push a page into a conversation they are not part of.
    public async Task<NoteResult<NoteShareDto>> AddShareAsync(Guid userId, Guid pageId, AddNoteShareRequest req, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Owner) return Fail<NoteShareDto>(Denied(access));
        if (!NoteLevels.All.Contains(req.Level)) return Fail<NoteShareDto>("invalid_level");
        if ((req.UserId is null) == (req.RoomId is null)) return Fail<NoteShareDto>("invalid_target");

        if (req.UserId is { } uid)
        {
            if (!await db.Users.AnyAsync(u => u.Id == uid && u.IsActive, ct)) return Fail<NoteShareDto>("user_not_found");
            var ownerId = await db.NotePages.Where(p => p.Id == pageId).Select(p => p.OwnerId).FirstAsync(ct);
            if (uid == ownerId) return Fail<NoteShareDto>("invalid_target");
        }
        if (req.RoomId is { } rid && !await db.ChatRoomMembers.AnyAsync(m => m.RoomId == rid && m.UserId == userId && m.LeftAt == null, ct))
            return Fail<NoteShareDto>("not_room_member");

        var share = await db.NoteShares.FirstOrDefaultAsync(s => s.PageId == pageId
            && (req.UserId != null ? s.UserId == req.UserId : s.RoomId == req.RoomId), ct);
        if (share is null)
        {
            share = new NoteShare
            {
                Id = Guid.NewGuid(), PageId = pageId, UserId = req.UserId, RoomId = req.RoomId, Level = req.Level,
                CreatedById = userId, CreatedAt = DateTime.UtcNow,
            };
            db.NoteShares.Add(share);
        }
        else share.Level = req.Level;
        await db.SaveChangesAsync(ct);

        var all = await GetSharesAsync(userId, pageId, ct);
        return Ok(all.Data!.Shares.First(s => s.Id == share.Id));
    }

    public async Task<NoteResult<bool>> UpdateShareAsync(Guid userId, Guid pageId, Guid shareId, string level, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Owner) return Fail<bool>(Denied(access));
        if (!NoteLevels.All.Contains(level)) return Fail<bool>("invalid_level");
        var n = await db.NoteShares.Where(s => s.Id == shareId && s.PageId == pageId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Level, level), ct);
        return n == 0 ? Fail<bool>("not_found") : Ok(true);
    }

    public async Task<NoteResult<bool>> RemoveShareAsync(Guid userId, Guid pageId, Guid shareId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Owner) return Fail<bool>(Denied(access));
        var n = await db.NoteShares.Where(s => s.Id == shareId && s.PageId == pageId).ExecuteDeleteAsync(ct);
        return n == 0 ? Fail<bool>("not_found") : Ok(true);
    }

    public async Task<NoteResult<NoteCardDto>> GetCardAsync(Guid userId, Guid pageId, string level, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<NoteCardDto>("not_found");
        var p = await db.NotePages.AsNoTracking().Where(x => x.Id == pageId)
            .Select(x => new { x.Title, x.Emoji, x.Cover, x.Accent, x.Excerpt }).FirstAsync(ct);
        return Ok(new NoteCardDto(pageId, p.Title, p.Emoji, p.Cover, p.Accent, level, p.Excerpt));
    }

    public async Task<IReadOnlyList<Guid>> GetAudienceAsync(Guid pageId, CancellationToken ct)
    {
        var chain = await ChainAsync(pageId, ct);
        var ids = chain.Select(n => n.Id).ToList();
        var users = chain.Select(n => n.OwnerId).ToHashSet();
        var shares = await db.NoteShares.AsNoTracking().Where(s => ids.Contains(s.PageId))
            .Select(s => new { s.UserId, s.RoomId }).ToListAsync(ct);
        foreach (var s in shares.Where(s => s.UserId != null)) users.Add(s.UserId!.Value);
        var rooms = shares.Where(s => s.RoomId != null).Select(s => s.RoomId!.Value).ToList();
        if (rooms.Count > 0)
            users.UnionWith(await db.ChatRoomMembers.AsNoTracking().Where(m => rooms.Contains(m.RoomId) && m.LeftAt == null)
                .Select(m => m.UserId).ToListAsync(ct));
        return users.ToList();
    }

    // ── Comments ─────────────────────────────────────────────────────────────

    private IQueryable<NoteThreadDto> ThreadQuery(Guid pageId, Guid? threadId = null) =>
        db.NoteThreads.AsNoTracking().Where(t => t.PageId == pageId && (threadId == null || t.Id == threadId)).OrderBy(t => t.CreatedAt)
            .Select(t => new NoteThreadDto(t.Id, t.Quote, t.Anchor, t.CreatedAt, t.ResolvedAt,
                t.Comments.OrderBy(c => c.CreatedAt).Select(c => new NoteCommentDto(c.Id, c.AuthorId,
                    c.Author == null ? "iPop Guide" : c.Author.Name, c.Author == null ? null : c.Author.Nik, c.Body, c.CreatedAt)).ToList()));

    public async Task<NoteResult<IReadOnlyList<NoteThreadDto>>> GetThreadsAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<IReadOnlyList<NoteThreadDto>>("not_found");
        return Ok<IReadOnlyList<NoteThreadDto>>(await ThreadQuery(pageId).ToListAsync(ct));
    }

    public async Task<NoteResult<NoteCommentResult>> CreateThreadAsync(Guid userId, Guid pageId, CreateNoteThreadRequest req, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Comment) return Fail<NoteCommentResult>(Denied(access));
        var body = Clean(req.Body, 2000);
        if (body is null) return Fail<NoteCommentResult>("empty");
        if (req.Anchor is { Length: > 2000 }) return Fail<NoteCommentResult>("invalid_anchor");

        var now = DateTime.UtcNow;
        var thread = new NoteThread
        {
            Id = Guid.NewGuid(), PageId = pageId, Quote = Clean(req.Quote, 300), Anchor = req.Anchor,
            CreatedById = userId, CreatedAt = now,
        };
        thread.Comments.Add(new NoteComment { Id = Guid.NewGuid(), ThreadId = thread.Id, AuthorId = userId, Body = body, CreatedAt = now });
        db.NoteThreads.Add(thread);
        await db.SaveChangesAsync(ct);
        return Ok(new NoteCommentResult(await ThreadQuery(pageId, thread.Id).FirstAsync(ct),
            await MentionTargetsAsync(userId, pageId, req.Mentions, ct)));
    }

    public async Task<NoteResult<NoteCommentResult>> ReplyAsync(Guid userId, Guid pageId, Guid threadId, NoteReplyRequest req, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Comment) return Fail<NoteCommentResult>(Denied(access));
        var body = Clean(req.Body, 2000);
        if (body is null) return Fail<NoteCommentResult>("empty");
        var thread = await db.NoteThreads.FirstOrDefaultAsync(t => t.Id == threadId && t.PageId == pageId, ct);
        if (thread is null) return Fail<NoteCommentResult>("not_found");

        db.NoteComments.Add(new NoteComment { Id = Guid.NewGuid(), ThreadId = threadId, AuthorId = userId, Body = body, CreatedAt = DateTime.UtcNow });
        thread.ResolvedAt = null;   // a reply reopens the thread
        thread.ResolvedById = null;
        await db.SaveChangesAsync(ct);

        // Everyone already in the thread hears about replies, plus anyone newly mentioned.
        var participants = await db.NoteComments.Where(c => c.ThreadId == threadId && c.AuthorId != null)
            .Select(c => c.AuthorId!.Value).Distinct().ToListAsync(ct);
        var notify = participants.Concat(req.Mentions ?? []).Distinct().ToList();
        return Ok(new NoteCommentResult(await ThreadQuery(pageId, threadId).FirstAsync(ct),
            await MentionTargetsAsync(userId, pageId, notify, ct)));
    }

    /// Mentioned users who can actually open the page (never the author).
    private async Task<IReadOnlyList<Guid>> MentionTargetsAsync(Guid userId, Guid pageId, IReadOnlyList<Guid>? mentions, CancellationToken ct)
    {
        var result = new List<Guid>();
        foreach (var id in (mentions ?? []).Distinct().Where(id => id != userId).Take(20))
            if (await GetAccessAsync(id, pageId, ct) > NoteAccess.None) result.Add(id);
        return result;
    }

    public async Task<NoteResult<bool>> SetResolvedAsync(Guid userId, Guid pageId, Guid threadId, bool resolved, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Comment) return Fail<bool>(Denied(access));
        var thread = await db.NoteThreads.FirstOrDefaultAsync(t => t.Id == threadId && t.PageId == pageId, ct);
        if (thread is null) return Fail<bool>("not_found");
        thread.ResolvedAt = resolved ? DateTime.UtcNow : null;
        thread.ResolvedById = resolved ? userId : null;
        await db.SaveChangesAsync(ct);
        return Ok(true);
    }

    public async Task<NoteResult<bool>> AddGuideThreadAsync(Guid userId, Guid pageId, string quote, string anchor, CancellationToken ct)
    {
        var page = await db.NotePages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId && p.IsGuide && p.OwnerId == userId, ct);
        if (page is null) return Fail<bool>("not_found");
        if (await db.NoteThreads.AnyAsync(t => t.PageId == pageId, ct)) return Ok(false);
        if (anchor.Length > 2000) return Fail<bool>("invalid_anchor");
        var now = DateTime.UtcNow;
        var thread = new NoteThread { Id = Guid.NewGuid(), PageId = pageId, Quote = Clean(quote, 300), Anchor = anchor, CreatedById = userId, CreatedAt = now };
        thread.Comments.Add(new NoteComment
        {
            Id = Guid.NewGuid(), ThreadId = thread.Id, AuthorId = null, CreatedAt = now,
            Body = "Ini contoh komentar 👋 Blok teks lalu klik 💬 untuk berdiskusi di bagian tertentu. Klik ✓ untuk menandai selesai.",
        });
        db.NoteThreads.Add(thread);
        await db.SaveChangesAsync(ct);
        return Ok(true);
    }

    // ── Attachments ──────────────────────────────────────────────────────────

    public async Task<NoteResult<bool>> CanUploadAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        return access >= NoteAccess.Edit ? Ok(true) : Fail<bool>(Denied(access));
    }

    public async Task LinkAttachmentAsync(Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        db.NoteAttachments.Add(new NoteAttachment { AttachmentId = attachmentId, PageId = pageId });
        await db.SaveChangesAsync(ct);
    }
}
