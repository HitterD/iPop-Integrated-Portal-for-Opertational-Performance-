using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// A page is either a document ("page") or a whiteboard ("board"). The kind is chosen when the page is created and then
/// stays: it decides which editor opens, so it must survive duplicate and show up wherever pages are listed.
public class NoteKindTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    [Fact]
    public async Task Pages_default_to_documents_and_boards_get_a_board_icon()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var doc = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Doc", null, null, null, null), Ct)).Data!;
        var board = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Board", null, null, null, null, "board"), Ct)).Data!;
        Assert.Equal("page", doc.Kind);
        Assert.Equal("board", board.Kind);
        Assert.Equal("🧩", board.Emoji);
        Assert.Null(doc.Emoji);
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_and_a_board_takes_no_text_template()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        Assert.False((await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "x", null, null, null, null, "spreadsheet"), Ct)).Success);
        var withTemplate = await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "x", null, "meeting", null, null, "board"), Ct);
        Assert.False(withTemplate.Success);
        Assert.Equal("invalid_template", withTemplate.Error);
    }

    [Fact]
    public async Task The_kind_is_in_the_sidebar_and_in_trash_and_survives_duplicate()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var board = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Retro", null, null, null, null, "board"), Ct)).Data!;
        var side = await Svc(t).GetSidebarAsync(a.Id, Ct);
        Assert.Equal("board", side.Pages.Single(p => p.Id == board.Id).Kind);

        var dup = await Svc(t).DuplicateAsync(a.Id, board.Id, Ct);
        Assert.True(dup.Success, dup.Error);
        Assert.Equal("board", dup.Data!.Kind);

        await Svc(t).TrashAsync(a.Id, board.Id, Ct);
        var trash = await Svc(t).GetTrashAsync(a.Id, Ct);
        Assert.Equal("board", trash.Single(p => p.Id == board.Id).Kind);
    }

    [Fact]
    public async Task A_board_can_be_shared_and_commented_like_any_page()
    {
        using var t = new TestDb();
        var a = t.AddUser("a"); var b = t.AddUser("b");
        var board = (await Svc(t).CreateAsync(a.Id, new CreateNoteRequest(null, "Retro", null, null, null, null, "board"), Ct)).Data!;
        Assert.True((await Svc(t).AddShareAsync(a.Id, board.Id, new AddNoteShareRequest(b.Id, null, NoteLevels.Edit), Ct)).Success);
        Assert.Equal("edit", (await Svc(t).GetPageAsync(b.Id, board.Id, Ct)).Data!.Access);
    }
}
