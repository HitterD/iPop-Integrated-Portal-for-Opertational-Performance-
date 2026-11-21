using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Comments: you can edit and delete your own, the page owner can delete anyone's, anyone who can comment can react, and a
/// comment on the page itself (no selected text) is just a thread without an anchor.
public class NoteCommentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<(Guid Page, Guid Thread, Guid Comment)> StartAsync(TestDb t, Guid author, Guid owner, string? anchor = null)
    {
        var page = (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, "P", null, null, null, null), Ct)).Data!.Id;
        if (author != owner) await Svc(t).AddShareAsync(owner, page, new AddNoteShareRequest(author, null, NoteLevels.Comment), Ct);
        var r = await Svc(t).CreateThreadAsync(author, page, new CreateNoteThreadRequest(anchor is null ? null : "quote", anchor, "First", null), Ct);
        Assert.True(r.Success, r.Error);
        return (page, r.Data!.Thread.Id, r.Data.Thread.Comments[0].Id);
    }

    [Fact]
    public async Task A_page_level_comment_has_no_anchor_and_appears_in_the_list()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var (page, _, _) = await StartAsync(t, a.Id, a.Id, anchor: null);
        var threads = (await Svc(t).GetThreadsAsync(a.Id, page, Ct)).Data!;
        Assert.Single(threads);
        Assert.Null(threads[0].Anchor);
        Assert.True(threads[0].Comments[0].CanEdit);
    }

    [Fact]
    public async Task Only_the_author_edits_a_comment_and_it_is_marked_edited()
    {
        using var t = new TestDb();
        var owner = t.AddUser("o"); var other = t.AddUser("x");
        var (page, _, comment) = await StartAsync(t, other.Id, owner.Id);

        Assert.Equal("forbidden", (await Svc(t).EditCommentAsync(owner.Id, page, comment, "hijack", Ct)).Error);   // not even the page owner
        Assert.Equal("empty", (await Svc(t).EditCommentAsync(other.Id, page, comment, "   ", Ct)).Error);
        var ok = await Svc(t).EditCommentAsync(other.Id, page, comment, "Better wording", Ct);
        Assert.True(ok.Success, ok.Error);
        var c = ok.Data!.Comments[0];
        Assert.Equal("Better wording", c.Body);
        Assert.NotNull(c.EditedAt);
        // unchanged text is not an edit
        var again = await Svc(t).EditCommentAsync(other.Id, page, comment, "Better wording", Ct);
        Assert.Equal(c.EditedAt, again.Data!.Comments[0].EditedAt);
        // what the owner sees: not theirs to edit
        Assert.False((await Svc(t).GetThreadsAsync(owner.Id, page, Ct)).Data![0].Comments[0].CanEdit);
    }

    [Fact]
    public async Task The_author_and_the_page_owner_can_delete_but_other_commenters_cannot()
    {
        using var t = new TestDb();
        var owner = t.AddUser("o"); var author = t.AddUser("a"); var third = t.AddUser("t");
        var (page, thread, c1) = await StartAsync(t, author.Id, owner.Id);
        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(third.Id, null, NoteLevels.Comment), Ct);
        var reply = await Svc(t).ReplyAsync(owner.Id, page, thread, new NoteReplyRequest("Reply", null), Ct);
        var c2 = reply.Data!.Thread.Comments[1].Id;

        Assert.Equal("forbidden", (await Svc(t).DeleteCommentAsync(third.Id, page, c1, Ct)).Error);
        Assert.True((await Svc(t).DeleteCommentAsync(owner.Id, page, c1, Ct)).Success);                // owner removes someone else's
        var left = (await Svc(t).GetThreadsAsync(owner.Id, page, Ct)).Data!.Single();
        Assert.Single(left.Comments);
        Assert.Equal(c2, left.Comments[0].Id);

        var last = await Svc(t).DeleteCommentAsync(owner.Id, page, c2, Ct);                              // the last one takes the thread with it
        Assert.True(last.Success);
        Assert.Null(last.Data);
        Assert.Empty((await Svc(t).GetThreadsAsync(owner.Id, page, Ct)).Data!);
    }

    [Fact]
    public async Task Reactions_toggle_per_person_group_by_emoji_and_know_which_are_mine()
    {
        using var t = new TestDb();
        var owner = t.AddUser("o"); var b = t.AddUser("b");
        var (page, _, comment) = await StartAsync(t, owner.Id, owner.Id);
        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.Comment), Ct);

        await Svc(t).ToggleReactionAsync(owner.Id, page, comment, "👍", Ct);
        var two = await Svc(t).ToggleReactionAsync(b.Id, page, comment, "👍", Ct);
        await Svc(t).ToggleReactionAsync(b.Id, page, comment, "🎉", Ct);
        var seenByB = (await Svc(t).GetThreadsAsync(b.Id, page, Ct)).Data![0].Comments[0].Reactions!;
        Assert.Equal(2, seenByB.Single(r => r.Emoji == "👍").Count);
        Assert.True(seenByB.Single(r => r.Emoji == "👍").Mine);
        Assert.True(seenByB.Single(r => r.Emoji == "🎉").Mine);
        var seenByOwner = (await Svc(t).GetThreadsAsync(owner.Id, page, Ct)).Data![0].Comments[0].Reactions!;
        Assert.False(seenByOwner.Single(r => r.Emoji == "🎉").Mine);
        Assert.NotNull(two.Data);

        // toggling again removes it, and the emoji disappears when nobody uses it
        await Svc(t).ToggleReactionAsync(b.Id, page, comment, "🎉", Ct);
        Assert.DoesNotContain((await Svc(t).GetThreadsAsync(b.Id, page, Ct)).Data![0].Comments[0].Reactions!, r => r.Emoji == "🎉");
        Assert.Equal(1, await t.Verify().NoteCommentReactions.CountAsync(r => r.Emoji == "👍" && r.UserId == b.Id));
    }

    [Fact]
    public async Task Viewers_cannot_react_and_unknown_emoji_are_refused_and_deleting_a_comment_removes_its_reactions()
    {
        using var t = new TestDb();
        var owner = t.AddUser("o"); var viewer = t.AddUser("v");
        var (page, _, comment) = await StartAsync(t, owner.Id, owner.Id);
        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(viewer.Id, null, NoteLevels.View), Ct);

        Assert.Equal("forbidden", (await Svc(t).ToggleReactionAsync(viewer.Id, page, comment, "👍", Ct)).Error);
        Assert.False((await Svc(t).ToggleReactionAsync(owner.Id, page, comment, "💣", Ct)).Success);
        await Svc(t).ToggleReactionAsync(owner.Id, page, comment, "👍", Ct);
        await Svc(t).DeleteCommentAsync(owner.Id, page, comment, Ct);
        Assert.Equal(0, await t.Verify().NoteCommentReactions.CountAsync());
    }
}
