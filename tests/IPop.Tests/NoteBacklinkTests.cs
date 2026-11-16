using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// "Linked from": a page lists the pages that link to it, but only those the viewer may open.
public class NoteBacklinkTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, string title)
        => (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, title, null, null, null, null), Ct)).Data!.Id;

    private static async Task LinkAsync(TestDb t, Guid owner, Guid from, params Guid[] to)
    {
        var v = await t.Verify().NotePages.Where(p => p.Id == from).Select(p => p.StateVersion).FirstAsync();
        var r = await Svc(t).SnapshotAsync(owner, from, new NoteSnapshotRequest("AQ==", v, [], "x", 0, 0, null, to), Ct);
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public async Task A_page_lists_who_links_to_it_and_a_removed_link_disappears()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var target = await PageAsync(t, a.Id, "Target");
        var src = await PageAsync(t, a.Id, "Source");
        await LinkAsync(t, a.Id, src, target);
        Assert.Equal(["Source"], (await Svc(t).GetBacklinksAsync(a.Id, target, Ct)).Data!.Select(x => x.Title).ToArray());
        await LinkAsync(t, a.Id, src);
        Assert.Empty((await Svc(t).GetBacklinksAsync(a.Id, target, Ct)).Data!);
    }

    [Fact]
    public async Task A_private_linking_page_is_never_revealed_to_someone_who_cannot_open_it()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var target = await PageAsync(t, a.Id, "Target");
        await Svc(t).AddShareAsync(a.Id, target, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);
        var secret = await PageAsync(t, a.Id, "Secret plans");
        await LinkAsync(t, a.Id, secret, target);
        Assert.Single((await Svc(t).GetBacklinksAsync(a.Id, target, Ct)).Data!);
        Assert.Empty((await Svc(t).GetBacklinksAsync(b.Id, target, Ct)).Data!);
    }

    [Fact]
    public async Task Backlinks_of_a_page_you_cannot_open_are_refused_and_trashed_sources_are_hidden()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var target = await PageAsync(t, a.Id, "Target");
        Assert.False((await Svc(t).GetBacklinksAsync(b.Id, target, Ct)).Success);
        var src = await PageAsync(t, a.Id, "Source");
        await LinkAsync(t, a.Id, src, target);
        await Svc(t).TrashAsync(a.Id, src, Ct);
        Assert.Empty((await Svc(t).GetBacklinksAsync(a.Id, target, Ct)).Data!);
    }

    [Fact]
    public async Task Garbage_links_are_ignored_and_a_page_never_links_to_itself()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var p = await PageAsync(t, a.Id, "P");
        await LinkAsync(t, a.Id, p, p, Guid.NewGuid(), Guid.Empty);
        Assert.Equal(0, await t.Verify().NoteLinks.CountAsync());
    }
}
