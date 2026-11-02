using Xunit;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;

namespace IPop.Tests;

public class NoteServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, Guid? parent = null, string title = "Page")
    {
        var r = await new NoteService(t.Verify()).CreateAsync(owner, new CreateNoteRequest(parent, title, null, null, null, null), Ct);
        Assert.True(r.Success, r.Error);
        return r.Data!.Id;
    }

    private static NoteService Svc(TestDb t) => new(t.Verify());

    [Fact]
    public async Task Owner_of_parent_owns_children_and_strangers_see_nothing()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id);
        var child = await PageAsync(t, a.Id, root);

        Assert.Equal(NoteAccess.Owner, await Svc(t).GetAccessAsync(a.Id, child, Ct));
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(b.Id, child, Ct));
        Assert.Equal("not_found", (await Svc(t).GetPageAsync(b.Id, root, Ct)).Error);
    }

    [Fact]
    public async Task Share_is_inherited_by_subpages_and_a_subpage_can_only_add()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id);
        var child = await PageAsync(t, a.Id, root);
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        Assert.Equal(NoteAccess.View, await Svc(t).GetAccessAsync(b.Id, child, Ct));
        await Svc(t).AddShareAsync(a.Id, child, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);
        Assert.Equal(NoteAccess.Edit, await Svc(t).GetAccessAsync(b.Id, child, Ct));
        Assert.Equal(NoteAccess.View, await Svc(t).GetAccessAsync(b.Id, root, Ct));

        // The inherited grant is listed read-only on the child.
        var shares = (await Svc(t).GetSharesAsync(a.Id, child, Ct)).Data!.Shares;
        Assert.Contains(shares, s => s.InheritedFromId == root);
        Assert.Contains(shares, s => s.InheritedFromId == null && s.Level == NoteLevels.Edit);
    }

    [Fact]
    public async Task Room_share_follows_current_membership()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b"); var c = t.AddUser("c");
        var room = t.AddRoom("group", "Tim");
        t.AddMember(room.Id, a.Id); var mb = t.AddMember(room.Id, b.Id);
        var page = await PageAsync(t, a.Id);

        var r = await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(null, room.Id, NoteLevels.Comment), Ct);
        Assert.True(r.Success, r.Error);
        Assert.Equal(NoteAccess.Comment, await Svc(t).GetAccessAsync(b.Id, page, Ct));

        // A sidebar of a room member shows the page as shared.
        var side = await Svc(t).GetSidebarAsync(b.Id, Ct);
        Assert.Contains(side.Pages, p => p.Id == page && p.IsShared && !p.IsMine);

        await t.Db.ChatRoomMembers.Where(m => m.Id == mb.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow));
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(b.Id, page, Ct));

        // Only room members can push a page into a room.
        var other = await PageAsync(t, c.Id);
        Assert.Equal("not_room_member", (await Svc(t).AddShareAsync(c.Id, other, new AddNoteShareRequest(null, room.Id, NoteLevels.View), Ct)).Error);
    }

    [Fact]
    public async Task Only_owners_share_and_editors_cannot()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b"); var c = t.AddUser("c");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);
        Assert.Equal("forbidden", (await Svc(t).AddShareAsync(b.Id, page, new AddNoteShareRequest(c.Id, null, NoteLevels.Edit), Ct)).Error);
    }

    [Fact]
    public async Task Viewer_cannot_push_updates_and_editor_can()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);
        Assert.Equal("forbidden", (await Svc(t).AppendUpdateAsync(b.Id, page, [1, 2, 3], Ct)).Error);

        var id = await Svc(t).AppendUpdateAsync(a.Id, page, [1, 2, 3], Ct);
        Assert.True(id.Success);
        var doc = (await Svc(t).GetDocAsync(b.Id, page, Ct)).Data!;
        Assert.Single(doc.Updates);
        Assert.Equal(0, doc.Version);
    }

    [Fact]
    public async Task Snapshot_on_stale_version_is_refused_and_keeps_unmerged_updates()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var u1 = (await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct)).Data;
        var u2 = (await Svc(t).AppendUpdateAsync(a.Id, page, [2], Ct)).Data;

        var ok = await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest(Convert.ToBase64String([9]), 0, [u1], "hello", 1, 3), Ct);
        Assert.Equal(1, ok.Data!.Version);

        var stale = await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest(Convert.ToBase64String([8]), 0, [u2], "x", 0, 0), Ct);
        Assert.Equal("version_conflict", stale.Error);

        var doc = (await Svc(t).GetDocAsync(a.Id, page, Ct)).Data!;
        Assert.Equal(1, doc.Version);
        Assert.Equal(Convert.ToBase64String([9]), doc.State);
        Assert.Equal([u2], doc.Updates.Select(u => u.Id));

        var p = await t.Verify().NotePages.SingleAsync(x => x.Id == page);
        Assert.Equal((1, 3), (p.TodoDone, p.TodoTotal));
        Assert.Equal("hello", p.Excerpt);
    }

    [Fact]
    public async Task Versions_are_recorded_at_most_every_ten_minutes()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var t0 = DateTime.UtcNow;
        async Task Edit(long v, DateTime at)
        {
            await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct);
            Assert.True((await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest("AQ==", v, [], "", 0, 0), Ct, at)).Success);
        }
        await Edit(0, t0);
        await Edit(1, t0.AddMinutes(3));
        await Edit(2, t0.AddMinutes(11));

        var versions = (await Svc(t).GetVersionsAsync(a.Id, page, Ct)).Data!;
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.Equal(["User a"], v.Contributors));
    }

    [Fact]
    public async Task Seed_template_is_claimed_exactly_once()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var r = await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Rapat", null, "meeting", null, null), Ct);
        Assert.Equal("meeting", (await Svc(t).ClaimSeedAsync(a.Id, r.Data!.Id, Ct)).Data);
        Assert.Null((await Svc(t).ClaimSeedAsync(a.Id, r.Data!.Id, Ct)).Data);
        Assert.Equal("invalid_template", (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "x", null, "guide", null, null), Ct)).Error);
    }

    // A page that already has edits (a collaborator typed first, or a duplicate carried the
    // content along) must not get the template dropped on top; the claim still clears it.
    [Fact]
    public async Task Seed_template_is_withheld_once_page_has_content()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var r = await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Rapat", null, "meeting", null, null), Ct);
        t.Db.NoteUpdates.Add(new IPop.Core.Entities.NoteUpdate { PageId = r.Data!.Id, UserId = a.Id, Data = [1, 2, 3], CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        Assert.Null((await Svc(t).ClaimSeedAsync(a.Id, r.Data!.Id, Ct)).Data);
        Assert.Null(await t.Verify().NotePages.Where(p => p.Id == r.Data!.Id).Select(p => p.SeedTemplate).SingleAsync());
    }

    [Fact]
    public async Task Guide_is_created_once_per_user()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var g1 = await Svc(t).EnsureGuideAsync(a.Id, Ct);
        var g2 = await Svc(t).EnsureGuideAsync(a.Id, Ct);
        Assert.Equal(g1, g2);
        var side = await Svc(t).GetSidebarAsync(a.Id, Ct);
        Assert.True(side.Pages.Single().IsGuide);
        Assert.Equal("guide", (await Svc(t).ClaimSeedAsync(a.Id, g1, Ct)).Data);
    }

    [Fact]
    public async Task Commenter_can_comment_but_viewer_cannot_and_mentions_need_access()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b"); var c = t.AddUser("c"); var d = t.AddUser("d");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Comment), Ct);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(c.Id, null, NoteLevels.View), Ct);

        Assert.Equal("forbidden", (await Svc(t).CreateThreadAsync(c.Id, page, new CreateNoteThreadRequest(null, null, "hai", null), Ct)).Error);
        var r = await Svc(t).CreateThreadAsync(b.Id, page, new CreateNoteThreadRequest("teks", "{}", "cek @a @d", [a.Id, d.Id, b.Id]), Ct);
        Assert.True(r.Success, r.Error);
        Assert.Equal([a.Id], r.Data!.NotifyUserIds);

        // Replies notify earlier participants.
        var reply = await Svc(t).ReplyAsync(a.Id, page, r.Data.Thread.Id, new NoteReplyRequest("ok", null), Ct);
        Assert.Equal([b.Id], reply.Data!.NotifyUserIds);
        Assert.Equal(2, reply.Data.Thread.Comments.Count);
    }

    [Fact]
    public async Task Trash_hides_subtree_and_restore_brings_it_back()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id);
        var child = await PageAsync(t, a.Id, root);
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);

        Assert.True((await Svc(t).TrashAsync(b.Id, root, Ct)).Success);
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(a.Id, child, Ct));
        Assert.Empty((await Svc(t).GetSidebarAsync(a.Id, Ct)).Pages);
        Assert.Single(await Svc(t).GetTrashAsync(a.Id, Ct));

        Assert.True((await Svc(t).RestoreAsync(a.Id, root, Ct)).Success);
        Assert.Equal(NoteAccess.Owner, await Svc(t).GetAccessAsync(a.Id, child, Ct));

        await Svc(t).TrashAsync(a.Id, root, Ct);
        Assert.True((await Svc(t).PurgeAsync(a.Id, root, Ct)).Success);
        Assert.False(await t.Verify().NotePages.AnyAsync());
    }

    [Fact]
    public async Task Search_matches_title_and_body_of_visible_pages_only()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var p = await PageAsync(t, a.Id, title: "Rencana Kuartal");
        await Svc(t).SnapshotAsync(a.Id, p, new NoteSnapshotRequest("AQ==", 0, [], "target penjualan naik", 0, 0), Ct);

        Assert.Single(await Svc(t).SearchAsync(a.Id, "kuartal", Ct));
        Assert.Contains("penjualan", (await Svc(t).SearchAsync(a.Id, "PENJUALAN", Ct)).Single().Snippet);
        Assert.Empty(await Svc(t).SearchAsync(b.Id, "kuartal", Ct));
    }

    [Fact]
    public async Task Move_reparents_for_owner_and_refuses_cycles_and_non_editable_targets()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id, null, "Root");
        var child = await PageAsync(t, a.Id, root, "Child");
        var other = await PageAsync(t, a.Id, null, "Other");

        Assert.Equal("invalid_move", (await Svc(t).MoveAsync(a.Id, root, new MoveNoteRequest(child, null), Ct)).Error);
        Assert.True((await Svc(t).MoveAsync(a.Id, child, new MoveNoteRequest(other, null), Ct)).Success);
        Assert.Equal(other, (await Svc(t).GetPageAsync(a.Id, child, Ct)).Data!.ParentId);

        // An editor (not owner) may reorder but not re-parent.
        await Svc(t).AddShareAsync(a.Id, other, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);
        Assert.Equal("confirm_audience", (await Svc(t).MoveAsync(a.Id, root, new MoveNoteRequest(other, null), Ct)).Error);
        Assert.Equal("forbidden", (await Svc(t).MoveAsync(b.Id, child, new MoveNoteRequest(null, null), Ct)).Error);
        var bPage = await PageAsync(t, b.Id, null, "B's");
        Assert.Equal("forbidden", (await Svc(t).MoveAsync(b.Id, bPage, new MoveNoteRequest(root, null), Ct)).Error);
    }

    [Fact]
    public async Task Move_that_changes_audience_needs_confirm()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var shared = await PageAsync(t, a.Id, null, "Shared");
        var priv = await PageAsync(t, a.Id, null, "Private");
        var page = await PageAsync(t, a.Id, shared, "Page");
        await Svc(t).AddShareAsync(a.Id, shared, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        Assert.Equal("confirm_audience", (await Svc(t).MoveAsync(a.Id, page, new MoveNoteRequest(priv, null), Ct)).Error);
        Assert.Equal(NoteAccess.View, await Svc(t).GetAccessAsync(b.Id, page, Ct));
        Assert.True((await Svc(t).MoveAsync(a.Id, page, new MoveNoteRequest(priv, null, true), Ct)).Success);
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(b.Id, page, Ct));
    }

    [Fact]
    public async Task Reorder_places_before_sibling_and_editor_can_reorder()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id, null, "Root");
        var x = await PageAsync(t, a.Id, root, "A");
        var y = await PageAsync(t, a.Id, root, "B");
        var z = await PageAsync(t, a.Id, root, "C");
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);

        Assert.True((await Svc(t).MoveAsync(b.Id, z, new MoveNoteRequest(root, x), Ct)).Success);
        var kids = (await Svc(t).GetPageAsync(a.Id, root, Ct)).Data!.Children.Select(c => c.Id).ToList();
        Assert.Equal([z, x, y], kids);

        Assert.True((await Svc(t).MoveAsync(a.Id, z, new MoveNoteRequest(root, null), Ct)).Success);
        kids = (await Svc(t).GetPageAsync(a.Id, root, Ct)).Data!.Children.Select(c => c.Id).ToList();
        Assert.Equal([x, y, z], kids);
        var side = (await Svc(t).GetSidebarAsync(a.Id, Ct)).Pages;
        Assert.Equal(3, side.Single(p => p.Id == z).SortOrder);
    }

    [Fact]
    public async Task Duplicate_copies_page_only_privately()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var root = await PageAsync(t, a.Id, null, "Plan");
        await PageAsync(t, a.Id, root, "Sub");
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);
        await Svc(t).AppendUpdateAsync(a.Id, root, [1, 2, 3], Ct);

        // An editor duplicates a shared top-level page: the copy is theirs, at their top level, unshared.
        var r = await Svc(t).DuplicateAsync(b.Id, root, Ct);
        Assert.True(r.Success, r.Error);
        Assert.Equal("Copy of Plan", r.Data!.Title);
        Assert.Null(r.Data.ParentId);
        Assert.Empty(r.Data.Children);
        Assert.False(r.Data.IsShared);
        Assert.Equal(NoteAccess.None, await Svc(t).GetAccessAsync(a.Id, r.Data.Id, Ct));
        Assert.Single((await Svc(t).GetDocAsync(b.Id, r.Data.Id, Ct)).Data!.Updates);

        var view = t.AddUser("v");
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(view.Id, null, NoteLevels.View), Ct);
        Assert.Equal("forbidden", (await Svc(t).DuplicateAsync(view.Id, root, Ct)).Error);
    }

    [Fact]
    public async Task People_search_for_a_page_flags_who_can_open_it()
    {
        using var t = new TestDb();
        var a = t.AddUser("pa"); var b = t.AddUser("pb"); var c = t.AddUser("pc");
        var root = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        var rows = await Svc(t).SearchPeopleAsync("user p", Ct, a.Id, root);
        Assert.True(rows.Single(r => r.Id == b.Id).HasAccess);
        Assert.False(rows.Single(r => r.Id == c.Id).HasAccess);
        Assert.All(rows, r => Assert.Null(r.Position));

        // Without a page (share dialog) or for someone who can't open it: no access flags at all.
        Assert.All(await Svc(t).SearchPeopleAsync("user p", Ct), r => Assert.Null(r.HasAccess));
        Assert.All(await Svc(t).SearchPeopleAsync("user p", Ct, c.Id, root), r => Assert.Null(r.HasAccess));
    }

    [Fact]
    public async Task Access_check_returns_only_people_who_can_open_the_page()
    {
        using var t = new TestDb();
        var a = t.AddUser("qa"); var b = t.AddUser("qb"); var c = t.AddUser("qc");
        var root = await PageAsync(t, a.Id);
        var child = await PageAsync(t, a.Id, root);
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.Comment), Ct);

        var ok = await Svc(t).WhoCanAccessAsync(a.Id, child, [a.Id, b.Id, c.Id], Ct);
        Assert.True(ok.Success);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), ok.Data!.OrderBy(x => x));
        // A stranger can't ask about a page they can't open.
        Assert.False((await Svc(t).WhoCanAccessAsync(c.Id, child, [a.Id], Ct)).Success);
    }

    [Fact]
    public async Task Mentioning_someone_without_access_notifies_nobody()
    {
        using var t = new TestDb();
        var a = t.AddUser("ma"); var b = t.AddUser("mb"); var c = t.AddUser("mc");
        var root = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, root, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        var r = await Svc(t).MentionInDocAsync(a.Id, root, [b.Id, c.Id], Ct);
        Assert.Equal(new[] { b.Id }, r.Data);
    }

    [Fact]
    public async Task People_search_matches_every_word_and_ranks_name_starts_first()
    {
        using var t = new TestDb();
        var me = t.AddUser("s0");
        foreach (var (nik, name) in new[] { ("s1", "AGUS WAHYUDI"), ("s2", "ARI WAHYUDI"), ("s3", "YUDI ARTA TRIRENSILA"), ("s4", "BUDI YUDISTIRA") })
        {
            var u = t.AddUser(nik); u.Name = name;
        }
        t.Db.SaveChanges();

        var yudi = await Svc(t).SearchPeopleAsync("yudi", Ct);
        Assert.Equal("YUDI ARTA TRIRENSILA", yudi[0].Name);          // name starts with it
        Assert.Equal("BUDI YUDISTIRA", yudi[1].Name);                // a word starts with it
        Assert.Contains(yudi, p => p.Name == "AGUS WAHYUDI");        // contains it: still listed, after

        var full = await Svc(t).SearchPeopleAsync("yudi arta", Ct);  // spaces: every word must match
        Assert.Equal("YUDI ARTA TRIRENSILA", Assert.Single(full).Name);
        Assert.Equal("YUDI ARTA TRIRENSILA", Assert.Single(await Svc(t).SearchPeopleAsync("arta yudi", Ct)).Name);
    }

    [Fact]
    public async Task Comment_mentions_are_resolved_from_body_even_when_mentions_list_is_null()
    {
        using var t = new TestDb();
        var a = t.AddUser("ca"); var b = t.AddUser("cb"); var c = t.AddUser("cc"); var d = t.AddUser("cd");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Comment), Ct);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(c.Id, null, NoteLevels.View), Ct);

        var r = await Svc(t).CreateThreadAsync(b.Id, page, new CreateNoteThreadRequest("q", "{}", "tolong cek @User ca dan @cd", null), Ct);
        Assert.True(r.Success, r.Error);
        Assert.Equal([a.Id], r.Data!.NotifyUserIds);

        var reply = await Svc(t).ReplyAsync(a.Id, page, r.Data.Thread.Id, new NoteReplyRequest("halo @cc!", null), Ct);
        Assert.True(reply.Success, reply.Error);
        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), reply.Data!.NotifyUserIds.OrderBy(x => x));
    }

    [Fact]
    public async Task Duplicate_clones_attachments_and_purge_soft_deletes_purged_page_attachments()
    {
        using var t = new TestDb();
        var a = t.AddUser("da"); var b = t.AddUser("db");
        var page = await PageAsync(t, a.Id, null, "With Image");
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);

        var attId = Guid.NewGuid();
        t.Db.Attachments.Add(new IPop.Core.Entities.Attachment
        {
            Id = attId, Area = "notes", FileName = "pic.png", ContentType = "image/png",
            Size = 42, Sha256 = "abc", StoragePath = "local:notes/pic.png", UploaderId = a.Id, CreatedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();
        await Svc(t).LinkAttachmentAsync(page, attId, Ct);

        var payload = System.Text.Encoding.ASCII.GetBytes($"prefix:{attId:D}:suffix");
        await Svc(t).AppendUpdateAsync(a.Id, page, payload, Ct);
        await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest(Convert.ToBase64String(payload), 0, [], "img", 0, 0), Ct);

        var dup = await Svc(t).DuplicateAsync(b.Id, page, Ct);
        Assert.True(dup.Success, dup.Error);
        var dupAttId = await t.Verify().NoteAttachments.Where(x => x.PageId == dup.Data!.Id).Select(x => x.AttachmentId).SingleAsync();
        Assert.NotEqual(attId, dupAttId);

        var dupDoc = (await Svc(t).GetDocAsync(b.Id, dup.Data!.Id, Ct)).Data!;
        var dupStateText = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(dupDoc.State!));
        Assert.Contains(dupAttId.ToString("D"), dupStateText);
        Assert.DoesNotContain(attId.ToString("D"), dupStateText);

        // Owner purges original page: original attachment is soft-deleted, duplicate attachment remains readable.
        await Svc(t).TrashAsync(a.Id, page, Ct);
        Assert.True((await Svc(t).PurgeAsync(a.Id, page, Ct)).Success);

        var policy = new NotesAttachmentPolicy(t.Verify(), Svc(t));
        Assert.False(await policy.CanReadAsync(attId, a.Id, Ct));
        Assert.True(await policy.CanReadAsync(dupAttId, b.Id, Ct));
    }

    [Fact]
    public async Task Versions_are_pruned_beyond_fifty_snapshots()
    {
        using var t = new TestDb();
        var a = t.AddUser("vp");
        var page = await PageAsync(t, a.Id);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 55; i++)
        {
            await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct);
            var r = await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest("AQ==", i, [], "", 0, 0), Ct, t0.AddMinutes(i * 11));
            Assert.True(r.Success, r.Error);
        }
        Assert.Equal(50, await t.Verify().NoteVersions.CountAsync(v => v.PageId == page));
    }
}
