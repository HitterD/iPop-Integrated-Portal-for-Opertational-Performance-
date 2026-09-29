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
}
