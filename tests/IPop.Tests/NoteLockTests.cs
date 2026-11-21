using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// A locked page refuses every change - typing, snapshots, style, title - from everyone, until someone with edit access
/// unlocks it. Reading, commenting and exporting stay available.
public class NoteLockTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner)
        => (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, "P", null, null, null, null), Ct)).Data!.Id;

    [Fact]
    public async Task A_locked_page_refuses_typing_snapshots_and_style_changes_from_everyone_including_the_owner()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        Assert.True((await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct)).Success);

        var locked = await Svc(t).SetLockedAsync(a.Id, page, true, Ct);
        Assert.True(locked.Success, locked.Error);
        Assert.NotNull(locked.Data!.LockedAt);
        Assert.False(string.IsNullOrWhiteSpace(locked.Data.LockedByName));   // the bar says who locked it

        Assert.Equal("locked", (await Svc(t).AppendUpdateAsync(a.Id, page, [2], Ct)).Error);
        Assert.Equal("locked", (await Svc(t).SnapshotAsync(a.Id, page, new NoteSnapshotRequest("AQ==", 0, [], "", 0, 0), Ct)).Error);
        Assert.Equal("locked", (await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest("New title", null, null, null, null, null, null), Ct)).Error);
        Assert.Equal("locked", (await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, "ocean", null, null, null, null), Ct)).Error);
        // nothing was written
        Assert.Single((await Svc(t).GetDocAsync(a.Id, page, Ct)).Data!.Updates);
        Assert.Equal("P", (await Svc(t).GetPageAsync(a.Id, page, Ct)).Data!.Title);
    }

    [Fact]
    public async Task Unlocking_restores_editing_and_an_editor_may_lock_and_unlock()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct);

        Assert.True((await Svc(t).SetLockedAsync(b.Id, page, true, Ct)).Success);
        Assert.Equal("locked", (await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct)).Error);
        var after = await Svc(t).SetLockedAsync(a.Id, page, false, Ct);
        Assert.True(after.Success);
        Assert.Null(after.Data!.LockedAt);
        Assert.True((await Svc(t).AppendUpdateAsync(a.Id, page, [1], Ct)).Success);
    }

    [Fact]
    public async Task Viewers_and_commenters_cannot_lock_or_unlock_and_strangers_do_not_see_the_page()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var v = t.AddUser("v"); var c = t.AddUser("c"); var s = t.AddUser("s");
        var page = await PageAsync(t, a.Id);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(v.Id, null, NoteLevels.View), Ct);
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(c.Id, null, NoteLevels.Comment), Ct);
        await Svc(t).SetLockedAsync(a.Id, page, true, Ct);

        Assert.Equal("forbidden", (await Svc(t).SetLockedAsync(v.Id, page, false, Ct)).Error);
        Assert.Equal("forbidden", (await Svc(t).SetLockedAsync(c.Id, page, false, Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).SetLockedAsync(s.Id, page, false, Ct)).Error);
        Assert.NotNull((await Svc(t).GetPageAsync(v.Id, page, Ct)).Data!.LockedAt);   // everyone sees the lock
    }

    [Fact]
    public async Task Locking_twice_keeps_the_first_lock_and_a_locked_page_can_still_be_duplicated_unlocked()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var first = (await Svc(t).SetLockedAsync(a.Id, page, true, Ct)).Data!.LockedAt;
        var again = (await Svc(t).SetLockedAsync(a.Id, page, true, Ct)).Data!.LockedAt;
        Assert.Equal(first, again);

        var dup = await Svc(t).DuplicateAsync(a.Id, page, Ct);
        Assert.True(dup.Success, dup.Error);
        Assert.Null(dup.Data!.LockedAt);
        Assert.True(await t.Verify().NotePages.AnyAsync(p => p.Id == page && p.LockedAt != null));
    }
}
