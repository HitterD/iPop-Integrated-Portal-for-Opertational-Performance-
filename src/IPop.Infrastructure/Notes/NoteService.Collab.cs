using IPop.Application.Attachments;
using IPop.Application.Notes;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

public sealed partial class NoteService
{
    private const int MaxVersions = 200; // list cap only; StaleVersions bounds what is stored

    public readonly record struct VersionStamp(Guid Id, DateTime CreatedAt, bool Named = false);

    /// Which versions to drop: everything from the last 7 days stays, then one per day (the newest of that day) up to
    /// 90 days, then one per ISO week. Applied on every new version, so history stays dense where people look and the table
    /// stops growing without bound (each version stores the full document state).
    public static IEnumerable<Guid> StaleVersions(IReadOnlyCollection<VersionStamp> versions, DateTime now)
    {
        var keep = new HashSet<Guid>();
        var buckets = new HashSet<string>();
        foreach (var v in versions.OrderByDescending(v => v.CreatedAt))
        {
            var age = now - v.CreatedAt;
            if (v.Named) { keep.Add(v.Id); continue; }   // a version someone named is never thinned out
            if (age <= TimeSpan.FromDays(7)) { keep.Add(v.Id); continue; }
            var d = v.CreatedAt.Date;
            var bucket = age <= TimeSpan.FromDays(90)
                ? "d" + d.ToString("yyyyMMdd")
                : "w" + System.Globalization.ISOWeek.GetYear(d) + "-" + System.Globalization.ISOWeek.GetWeekOfYear(d);
            if (buckets.Add(bucket)) keep.Add(v.Id);
        }
        return versions.Where(v => !keep.Contains(v.Id)).Select(v => v.Id);
    }

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
        if (await db.NotePages.AsNoTracking().AnyAsync(p => p.Id == pageId && p.LockedAt != null, ct)) return Fail<long>("locked");

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
        if (await db.NotePages.AsNoTracking().AnyAsync(p => p.Id == pageId && p.LockedAt != null, ct)) return Fail<NoteSnapshotResultDto>("locked");
        byte[] state;
        try { state = Convert.FromBase64String(req.State); }
        catch (FormatException) { return Fail<NoteSnapshotResultDto>("invalid_update"); }
        if (state.Length == 0 || state.Length > 16 * 1024 * 1024) return Fail<NoteSnapshotResultDto>("invalid_update");

        var at = now ?? DateTime.UtcNow;
        var next = req.BaseVersion + 1;
        // The snapshot is sent on tab close, so the request can be aborted at any point: never with the caller's token,
        // and all of it in one transaction - merged updates used to be deleted BEFORE the new state was saved, so an
        // abort in between lost those edits.
        ct = CancellationToken.None;
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // Compare-and-swap on the version: only one snapshot per base version can win.
        var won = await db.NotePages.Where(p => p.Id == pageId && p.StateVersion == req.BaseVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StateVersion, next), ct);
        if (won == 0) { if (tx is not null) await tx.RollbackAsync(ct); return Fail<NoteSnapshotResultDto>("version_conflict"); }

        var page = await db.NotePages.FirstAsync(p => p.Id == pageId, ct);
        await db.Entry(page).ReloadAsync(ct);
        page.YState = state;
        var text = req.PlainText ?? "";
        page.PlainText = text; // Search indexes the complete editor text, not just its preview.
        var flat = string.Join(' ', text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        page.Excerpt = flat.Length > 300 ? flat[..299] + "…" : flat;
        page.TodoDone = Math.Clamp(req.TodoDone, 0, 10000);
        page.TodoTotal = Math.Clamp(req.TodoTotal, 0, 10000);

        var merged = req.MergedUpdateIds.Distinct().ToList();

        var last = await db.NoteVersions.Where(v => v.PageId == pageId).MaxAsync(v => (DateTime?)v.CreatedAt, ct);
        var wroteVersion = false;
        if (page.PendingContributors is not null && (last is null || at - last.Value >= VersionEvery))
        {
            db.NoteVersions.Add(new NoteVersion
            {
                Id = Guid.NewGuid(), PageId = pageId, State = state, Title = page.Title,
                Contributors = page.PendingContributors, CreatedAt = at,
            });
            page.PendingContributors = null;
            wroteVersion = true;
        }
        await LinkReferencedAttachmentsAsync(pageId, state, ct);
        if (req.TodoDue is not null) await SyncTodoDueAsync(pageId, req.TodoDue, ct);
        if (req.LinksTo is not null) await SyncLinksAsync(pageId, req.LinksTo, ct);
        await db.SaveChangesAsync(ct);
        // Only once the new state is stored are the updates it contains removed (same transaction).
        if (merged.Count > 0)
            await db.NoteUpdates.Where(u => u.PageId == pageId && merged.Contains(u.Id)).ExecuteDeleteAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        if (wroteVersion)
        {
            var all = await db.NoteVersions.AsNoTracking().Where(v => v.PageId == pageId)
                .Select(v => new VersionStamp(v.Id, v.CreatedAt, v.Name != null)).ToListAsync(ct);
            var staleIds = StaleVersions(all, at).ToList();
            if (staleIds.Count > 0)
                await db.NoteVersions.Where(v => staleIds.Contains(v.Id)).ExecuteDeleteAsync(ct);
        }
        return Ok(new NoteSnapshotResultDto(next));
    }

    /// Replaces the page's outgoing page links with what the saved content holds. Only links to real, live pages are kept,
    /// never to itself; the list is capped so one page cannot flood the table.
    private async Task SyncLinksAsync(Guid pageId, IReadOnlyList<Guid> links, CancellationToken ct)
    {
        var wanted = links.Where(l => l != pageId).Distinct().Take(100).ToList();
        var real = wanted.Count == 0 ? [] : await db.NotePages.AsNoTracking().Where(p => wanted.Contains(p.Id) && p.DeletedAt == null).Select(p => p.Id).ToListAsync(ct);
        var existing = await db.NoteLinks.Where(l => l.FromPageId == pageId).ToListAsync(ct);
        foreach (var row in existing.Where(r => !real.Contains(r.ToPageId))) db.NoteLinks.Remove(row);
        foreach (var to in real.Where(r => existing.All(e => e.ToPageId != r))) db.NoteLinks.Add(new NoteLink { FromPageId = pageId, ToPageId = to });
    }

    public async Task<NoteResult<IReadOnlyList<NoteBacklinkDto>>> GetBacklinksAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        if (await GetAccessAsync(userId, pageId, ct) == NoteAccess.None) return Fail<IReadOnlyList<NoteBacklinkDto>>("not_found");
        var from = await db.NoteLinks.AsNoTracking().Where(l => l.ToPageId == pageId).Select(l => l.FromPageId).ToListAsync(ct);
        if (from.Count == 0) return Ok<IReadOnlyList<NoteBacklinkDto>>([]);
        // Only pages the viewer can open: a link from a private page must never reveal that page's title.
        var visible = await VisibleAsync(userId, ct);
        var ids = from.Where(visible.ContainsKey).ToList();
        var rows = await db.NotePages.AsNoTracking().Where(p => ids.Contains(p.Id) && p.DeletedAt == null)
            .OrderByDescending(p => p.UpdatedAt).Take(30)
            .Select(p => new NoteBacklinkDto(p.Id, p.Title, p.Emoji, p.Excerpt)).ToListAsync(ct);
        return Ok<IReadOnlyList<NoteBacklinkDto>>(rows);
    }

    /// Replaces the page's due-dated to-dos with what the saved content now holds. An item that is still there keeps its
    /// "reminder already sent" marks (so saving the page never re-sends a reminder); an item whose due date or person
    /// changed starts over.
    private async Task SyncTodoDueAsync(Guid pageId, IReadOnlyList<NoteTodoDueDto> items, CancellationToken ct)
    {
        var existing = await db.NoteTodoDues.Where(x => x.PageId == pageId).ToListAsync(ct);
        var wanted = new Dictionary<string, NoteTodoDueDto>();
        foreach (var i in items.Take(200))
            if (!string.IsNullOrWhiteSpace(i.Key) && i.Key.Length <= 64 && DateOnly.TryParseExact(i.Due, "yyyy-MM-dd", out _)) wanted[i.Key] = i;
        foreach (var row in existing.Where(r => !wanted.ContainsKey(r.ItemKey))) db.NoteTodoDues.Remove(row);
        foreach (var (key, i) in wanted)
        {
            var due = DateOnly.ParseExact(i.Due, "yyyy-MM-dd");
            var text = Clean(i.Text, 200) ?? "";
            var row = existing.FirstOrDefault(r => r.ItemKey == key);
            if (row is null)
            {
                db.NoteTodoDues.Add(new NoteTodoDue { PageId = pageId, ItemKey = key, AssigneeId = i.AssigneeId, Due = due, Text = text, Done = i.Done });
                continue;
            }
            if (row.Due != due || row.AssigneeId != i.AssigneeId) { row.DayBeforeSentAt = null; row.DaySentAt = null; }
            row.AssigneeId = i.AssigneeId; row.Due = due; row.Text = text; row.Done = i.Done;
        }
    }

    public async Task<NoteResult<string?>> ClaimSeedAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<string?>(Denied(access));
        var template = await db.NotePages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.SeedTemplate).FirstAsync(ct);
        if (template is null) return Ok<string?>(null);
        var won = await db.NotePages.Where(p => p.Id == pageId && p.SeedTemplate == template)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SeedTemplate, (string?)null), ct);
        if (won != 1) return Ok<string?>(null);
        // Someone already typed into the page (another tab or collaborator raced the seed):
        // dropping the template on top would mix it into their text. The claim still clears it.
        var hasContent = await db.NotePages.AnyAsync(p => p.Id == pageId && p.PlainText != null && p.PlainText != "", ct)
            || await db.NoteUpdates.AnyAsync(u => u.PageId == pageId, ct);
        return Ok<string?>(hasContent ? null : template);
    }

    // ── Versions ─────────────────────────────────────────────────────────────

    public async Task<NoteResult<IReadOnlyList<NoteVersionDto>>> GetVersionsAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<IReadOnlyList<NoteVersionDto>>("not_found");
        var rows = await db.NoteVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderByDescending(v => v.CreatedAt)
            .Take(MaxVersions).Select(v => new { v.Id, v.CreatedAt, v.Title, v.Contributors, v.Name }).ToListAsync(ct);
        var ids = rows.SelectMany(r => ParseIds(r.Contributors)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return Ok<IReadOnlyList<NoteVersionDto>>(rows.Select(r => new NoteVersionDto(r.Id, r.CreatedAt, r.Title,
            ParseIds(r.Contributors).Where(names.ContainsKey).Select(id => names[id]).ToList(), r.Name)).ToList());
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
                s.Id, s.PageId, s.UserId, UserName = s.User!.Name, UserNik = s.User.Nik, UserPosition = (string?)null,
                s.RoomId, RoomType = s.Room!.Type, s.Level, PageTitle = s.Page.Title,
            }).ToListAsync(ct);

        var roomIds = rows.Where(r => r.RoomId != null).Select(r => r.RoomId!.Value).Distinct().ToList();
        var memberCounts = roomIds.Count == 0 ? [] : await db.ChatRoomMembers.AsNoTracking()
            .Where(m => roomIds.Contains(m.RoomId) && m.LeftAt == null)
            .GroupBy(m => m.RoomId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var shares = new List<NoteShareDto>();
        foreach (var r in rows)
        {
            string? roomName = null;
            var members = 0;
            if (r.RoomId is { } rid)
            {
                roomName = await RoomLabelAsync(rid, userId, ct);
                members = memberCounts.GetValueOrDefault(rid);
            }
            var inherited = r.PageId != pageId;
            shares.Add(new NoteShareDto(r.Id, r.UserId, r.UserId is null ? null : r.UserName, r.UserId is null ? null : r.UserNik,
                r.UserId is null ? null : r.UserPosition, r.RoomId, roomName, r.RoomId is null ? null : r.RoomType, members,
                r.Level, inherited ? r.PageId : null, inherited ? r.PageTitle : null));
        }
        return Ok(new NoteSharesDto(owner.OwnerId, owner.Name, owner.Nik, null, shares));
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
            .Select(x => new { x.Title, x.Emoji, x.Cover, x.Accent, x.Excerpt, x.CoverImageId, x.CoverPosY }).FirstAsync(ct);
        return Ok(new NoteCardDto(pageId, p.Title, p.Emoji, p.Cover, p.Accent, level, p.Excerpt, p.CoverImageId, p.CoverPosY));
    }

    public async Task<IReadOnlyList<Guid>> GetAudienceAsync(Guid pageId, CancellationToken ct)
    {
        var chain = await ChainAsync(pageId, ct);
        if (chain.Count == 0 || chain.Any(n => n.DeletedAt != null)) return [];
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

    /// Threads of a page as <paramref name="viewer"/> sees them: reactions carry "mine", and CanEdit says whether the viewer
    /// wrote the comment (only your own comments can be edited; deleting also needs the page owner, see DeleteCommentAsync).
    private async Task<List<NoteThreadDto>> ThreadsForAsync(Guid pageId, Guid viewer, Guid? threadId, CancellationToken ct)
    {
        var rows = await db.NoteThreads.AsNoTracking().Where(t => t.PageId == pageId && (threadId == null || t.Id == threadId)).OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id, t.Quote, t.Anchor, t.CreatedAt, t.ResolvedAt,
                Comments = t.Comments.OrderBy(c => c.CreatedAt).Select(c => new
                {
                    c.Id, c.AuthorId, Name = c.Author == null ? "iPop Guide" : c.Author.Name, Nik = c.Author == null ? null : c.Author.Nik,
                    c.Body, c.CreatedAt, c.EditedAt,
                    Reactions = c.Reactions.Select(r => new { r.Emoji, r.UserId, r.CreatedAt }).ToList(),
                }).ToList(),
            }).ToListAsync(ct);
        return rows.Select(t => new NoteThreadDto(t.Id, t.Quote, t.Anchor, t.CreatedAt, t.ResolvedAt,
            t.Comments.Select(c => new NoteCommentDto(c.Id, c.AuthorId, c.Name, c.Nik, c.Body, c.CreatedAt, c.EditedAt,
                c.Reactions.GroupBy(r => r.Emoji).OrderBy(g => g.Min(r => r.CreatedAt))
                    .Select(g => new NoteReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == viewer))).ToList(),
                c.AuthorId == viewer)).ToList())).ToList();
    }

    public async Task<NoteResult<IReadOnlyList<NoteThreadDto>>> GetThreadsAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<IReadOnlyList<NoteThreadDto>>("not_found");
        return Ok<IReadOnlyList<NoteThreadDto>>(await ThreadsForAsync(pageId, userId, null, ct));
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
        return Ok(new NoteCommentResult((await ThreadsForAsync(pageId, userId, thread.Id, ct)).First(),
            await MentionTargetsAsync(userId, pageId, req.Mentions, ct, body)));
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
        return Ok(new NoteCommentResult((await ThreadsForAsync(pageId, userId, threadId, ct)).First(),
            await MentionTargetsAsync(userId, pageId, notify, ct, body)));
    }

    public static readonly IReadOnlySet<string> ReactionEmojis = new HashSet<string> { "👍", "❤️", "🎉", "😂", "🙏", "👀" };

    private async Task<(NoteComment? Comment, NoteAccess Access, bool IsOwner)> CommentContextAsync(Guid userId, Guid pageId, Guid commentId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Comment) return (null, access, false);
        var c = await db.NoteComments.Include(x => x.Thread).FirstOrDefaultAsync(x => x.Id == commentId && x.Thread.PageId == pageId, ct);
        return (c, access, access == NoteAccess.Owner);
    }

    public async Task<NoteResult<NoteThreadDto>> EditCommentAsync(Guid userId, Guid pageId, Guid commentId, string body, CancellationToken ct)
    {
        var (c, access, _) = await CommentContextAsync(userId, pageId, commentId, ct);
        if (c is null) return Fail<NoteThreadDto>(Denied(access));
        if (c.AuthorId != userId) return Fail<NoteThreadDto>("forbidden");     // the guide comment has no author, so nobody edits it
        var clean = Clean(body, 2000);
        if (clean is null) return Fail<NoteThreadDto>("empty");
        if (clean != c.Body) { c.Body = clean; c.EditedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        return Ok((await ThreadsForAsync(pageId, userId, c.ThreadId, ct)).First());
    }

    public async Task<NoteResult<NoteThreadDto?>> DeleteCommentAsync(Guid userId, Guid pageId, Guid commentId, CancellationToken ct)
    {
        var (c, access, isOwner) = await CommentContextAsync(userId, pageId, commentId, ct);
        if (c is null) return Fail<NoteThreadDto?>(Denied(access));
        if (c.AuthorId != userId && !isOwner) return Fail<NoteThreadDto?>("forbidden");
        var threadId = c.ThreadId;
        db.NoteComments.Remove(c);
        await db.SaveChangesAsync(ct);
        if (!await db.NoteComments.AnyAsync(x => x.ThreadId == threadId, ct))
        {
            await db.NoteThreads.Where(t => t.Id == threadId).ExecuteDeleteAsync(ct);
            return Ok<NoteThreadDto?>(null);                                   // the whole thread is gone
        }
        return Ok<NoteThreadDto?>((await ThreadsForAsync(pageId, userId, threadId, ct)).First());
    }

    public async Task<NoteResult<NoteThreadDto>> ToggleReactionAsync(Guid userId, Guid pageId, Guid commentId, string emoji, CancellationToken ct)
    {
        var (c, access, _) = await CommentContextAsync(userId, pageId, commentId, ct);
        if (c is null) return Fail<NoteThreadDto>(Denied(access));
        if (!ReactionEmojis.Contains(emoji)) return Fail<NoteThreadDto>("invalid_style");
        var row = await db.NoteCommentReactions.FirstOrDefaultAsync(r => r.CommentId == commentId && r.UserId == userId && r.Emoji == emoji, ct);
        if (row is null) db.NoteCommentReactions.Add(new NoteCommentReaction { CommentId = commentId, UserId = userId, Emoji = emoji, CreatedAt = DateTime.UtcNow });
        else db.NoteCommentReactions.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok((await ThreadsForAsync(pageId, userId, c.ThreadId, ct)).First());
    }

    /// Mentioned users who can actually open the page (never the author). Also resolves @Name / @Nik in body.
    private async Task<IReadOnlyList<Guid>> MentionTargetsAsync(Guid userId, Guid pageId, IReadOnlyList<Guid>? mentions, CancellationToken ct, string? body = null)
    {
        var audience = (await GetAudienceAsync(pageId, ct)).ToHashSet();
        audience.Remove(userId);
        if (audience.Count == 0) return [];
        var ids = (mentions ?? []).Where(audience.Contains).ToHashSet();
        if (!string.IsNullOrEmpty(body) && body.Contains('@'))
        {
            var candidates = await db.Users.AsNoTracking()
                .Where(u => audience.Contains(u.Id) && u.IsActive)
                .Select(u => new { u.Id, u.Name, u.Nik }).ToListAsync(ct);
            foreach (var u in candidates)
            {
                if (HasMentionToken(body, u.Name) || HasMentionToken(body, u.Nik))
                    ids.Add(u.Id);
            }
        }
        return ids.Take(20).ToList();
    }

    private static bool HasMentionToken(string body, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var needle = "@" + token;
        var idx = 0;
        while ((idx = body.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = idx == 0 || char.IsWhiteSpace(body[idx - 1]) || char.IsPunctuation(body[idx - 1]);
            var afterIdx = idx + needle.Length;
            var afterOk = afterIdx >= body.Length || !char.IsLetterOrDigit(body[afterIdx]);
            if (beforeOk && afterOk) return true;
            idx = afterIdx;
        }
        return false;
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
            Body = "This is a sample comment 👋 Select text and click 💬 to discuss a specific part. Click ✓ to mark it resolved.",
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

    private static readonly System.Text.RegularExpressions.Regex GuidText = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// An image or file pasted from another page keeps its attachment id inside this page's content, but only the page it
    /// was uploaded to is linked. Link every notes attachment the saved content mentions, so people who can open THIS page
    /// can load it even when they cannot open the original (or the original is purged).
    private async Task LinkReferencedAttachmentsAsync(Guid pageId, byte[] state, CancellationToken ct)
    {
        var ids = GuidText.Matches(System.Text.Encoding.Latin1.GetString(state)).Select(m => Guid.Parse(m.Value)).Distinct().Take(500).ToList();
        if (ids.Count == 0) return;
        var known = await db.Attachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.Area == AttachmentAreas.Notes && a.DeletedAt == null).Select(a => a.Id).ToListAsync(ct);
        if (known.Count == 0) return;
        var linked = await db.NoteAttachments.AsNoTracking().Where(a => a.PageId == pageId && known.Contains(a.AttachmentId))
            .Select(a => a.AttachmentId).ToListAsync(ct);
        foreach (var id in known.Except(linked)) db.NoteAttachments.Add(new NoteAttachment { AttachmentId = id, PageId = pageId });
    }

    private async Task<bool> IsPageImageAsync(Guid pageId, Guid attachmentId, CancellationToken ct) =>
        await (from na in db.NoteAttachments.AsNoTracking()
               join a in db.Attachments.AsNoTracking() on na.AttachmentId equals a.Id
               where na.PageId == pageId && na.AttachmentId == attachmentId && a.DeletedAt == null && a.ContentType.StartsWith("image/")
               select a.Id).AnyAsync(ct);

    public async Task<NoteResult<NotePageDto>> SetCoverImageAsync(Guid userId, Guid pageId, Guid attachmentId, CancellationToken ct) =>
        await UpdateMetaAsync(userId, pageId, new UpdateNoteMetaRequest(null, null, null, null, null, null, null, attachmentId), ct);

    public async Task<NoteResult<IReadOnlyList<NoteImageDto>>> GetPageImagesAsync(Guid userId, Guid pageId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access == NoteAccess.None) return Fail<IReadOnlyList<NoteImageDto>>("not_found");
        var rows = await (from na in db.NoteAttachments.AsNoTracking()
                          join a in db.Attachments.AsNoTracking() on na.AttachmentId equals a.Id
                          where na.PageId == pageId && a.DeletedAt == null && a.ContentType.StartsWith("image/")
                          orderby a.CreatedAt descending
                          select new NoteImageDto(a.Id, a.FileName, a.Size)).Take(60).ToListAsync(ct);
        return Ok<IReadOnlyList<NoteImageDto>>(rows);
    }

    public async Task<NoteResult<Guid?>> SetThumbnailAsync(Guid userId, Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<Guid?>(Denied(access));
        var page = await db.NotePages.FirstOrDefaultAsync(p => p.Id == pageId && p.DeletedAt == null, ct);
        if (page is null) return Fail<Guid?>("not_found");
        if (page.Kind != "board") return Fail<Guid?>("not_a_board");
        // only a picture that was uploaded to THIS page can become its thumbnail
        var linked = await (from na in db.NoteAttachments.AsNoTracking()
                            join a in db.Attachments.AsNoTracking() on na.AttachmentId equals a.Id
                            where na.PageId == pageId && na.AttachmentId == attachmentId && a.DeletedAt == null && a.ContentType.StartsWith("image/")
                            select a.Id).AnyAsync(ct);
        if (!linked) return Fail<Guid?>("invalid_image");
        var old = page.ThumbnailId;
        page.ThumbnailId = attachmentId;
        page.ThumbnailAt = DateTime.UtcNow;
        // the thumbnail is bookkeeping, not an edit: it must not move "edited 2 min ago" or the page's UpdatedBy
        await db.SaveChangesAsync(ct);
        return Ok<Guid?>(old is { } o && o != attachmentId ? o : null);
    }

    public async Task LinkAttachmentAsync(Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        db.NoteAttachments.Add(new NoteAttachment { AttachmentId = attachmentId, PageId = pageId });
        await db.SaveChangesAsync(ct);
    }

    // ── People ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<NotePersonDto>> SearchPeopleAsync(string query, CancellationToken ct,
        Guid? userId = null, Guid? pageId = null)
    {
        var q = query.Trim().ToLower();
        if (q.Length < 2) return [];
        // Every typed word must appear (any order): "yudi arta" finds YUDI ARTA TRIRENSILA. Ranking puts
        // names that start with the query, then names with a word starting with it, ahead of names that
        // merely contain it ("yudi" → YUDI … before WAHYUDI).
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        var people = db.Users.AsNoTracking().Where(u => u.IsActive);
        foreach (var w in words)
            people = people.Where(u => u.Name.ToLower().Contains(w) || (u.Email != null && u.Email.ToLower().Contains(w)));
        var first = words[0];
        var rows = await people
            .OrderBy(u => u.Name.ToLower().StartsWith(q) ? 0
                : u.Name.ToLower().StartsWith(first) ? 1
                : u.Name.ToLower().Contains(" " + first) ? 2 : 3)
            .ThenBy(u => u.Name).Take(12)
            .Select(u => new { u.Id, u.Nik, u.Name, Department = u.Department != null ? u.Department.Name : null })
            .ToListAsync(ct);
        // Access flags only for someone who can open the page themselves (no probing of other pages).
        HashSet<Guid>? audience = userId is { } me && pageId is { } pid
            && (await GetAudienceAsync(pid, ct)).ToHashSet() is var set && set.Contains(me) ? set : null;
        var result = new List<NotePersonDto>(rows.Count);
        foreach (var u in rows)
            result.Add(new NotePersonDto(u.Id, u.Nik, u.Name, null, u.Department, audience?.Contains(u.Id)));
        return result;
    }

    public async Task<NoteResult<IReadOnlyList<Guid>>> WhoCanAccessAsync(Guid userId, Guid pageId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var audience = (await GetAudienceAsync(pageId, ct)).ToHashSet();
        if (!audience.Contains(userId)) return Fail<IReadOnlyList<Guid>>("not_found");
        var result = (userIds ?? []).Distinct().Take(100).Where(audience.Contains).ToList();
        return Ok<IReadOnlyList<Guid>>(result);
    }

    public async Task<NoteResult<IReadOnlyList<Guid>>> MentionInDocAsync(Guid userId, Guid pageId, IReadOnlyList<Guid> mentions, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, pageId, ct);
        if (access < NoteAccess.Edit) return Fail<IReadOnlyList<Guid>>(Denied(access));
        return Ok(await MentionTargetsAsync(userId, pageId, mentions, ct));
    }
}
