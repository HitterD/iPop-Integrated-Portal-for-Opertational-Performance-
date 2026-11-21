using IPop.Application.Notes;
using IPop.Core.Entities;
using IPop.Infrastructure.Calendar;
using IPop.Infrastructure.Notes;
using Xunit;

namespace IPop.Tests;

/// To-dos in Notes (a person + a due date) show on that person's calendar as all-day items, but only while they can open the note.
public class CalendarNoteTodoTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime From = DateTime.UtcNow.Date.AddDays(-3);
    private static readonly DateTime To = DateTime.UtcNow.Date.AddDays(30);
    private static DateOnly Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(offset));

    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, Guid? parent = null, string title = "Page")
        => (await Svc(t).CreateAsync(owner, new CreateNoteRequest(parent, title, null, null, null, null), Ct)).Data!.Id;

    private static void Todo(TestDb t, Guid page, Guid? who, DateOnly due, string text, bool done = false, string key = "k1")
    {
        t.Db.NoteTodoDues.Add(new NoteTodoDue { PageId = page, ItemKey = key, AssigneeId = who, Due = due, Text = text, Done = done });
        t.Db.SaveChanges();
    }

    private static Task<List<IPop.Application.Calendar.CalendarFeedItem>> Feed(TestDb t, Guid user) =>
        CalendarFeedService.BuildAsync(t.Db, user, From, To, new IPop.Application.Calendar.CalendarFeedAccess(true, true, true, true, true), Ct);

    [Fact]
    public async Task My_open_to_do_with_a_due_date_is_an_all_day_item_that_opens_the_note()
    {
        using var t = new TestDb();
        var me = t.AddUser("n1");
        var page = await PageAsync(t, me.Id, title: "Weekly ICT");
        Todo(t, page, me.Id, Day(2), "Send the minutes");

        var item = Assert.Single((await Feed(t, me.Id)).Where(i => i.Source == "notes"));
        Assert.Equal("note-todo", item.Kind);
        Assert.Equal("Send the minutes", item.Title);
        Assert.True(item.AllDay);
        Assert.Equal($"/notes/{page}", item.Url);
        Assert.Equal("To-do in Weekly ICT", item.Subtitle);
        Assert.False(item.CanEdit);
        Assert.Equal(Day(2).ToDateTime(TimeOnly.MinValue), item.StartAt);
    }

    [Fact]
    public async Task Only_mine_only_open_only_in_range()
    {
        using var t = new TestDb();
        var me = t.AddUser("n2"); var other = t.AddUser("n3");
        var page = await PageAsync(t, me.Id);
        Todo(t, page, me.Id, Day(1), "mine", key: "a");
        Todo(t, page, other.Id, Day(1), "somebody else", key: "b");
        Todo(t, page, me.Id, Day(1), "done", done: true, key: "c");
        Todo(t, page, me.Id, Day(90), "far away", key: "d");
        Todo(t, page, null, Day(1), "nobody", key: "e");

        var titles = (await Feed(t, me.Id)).Where(i => i.Source == "notes").Select(i => i.Title).ToList();
        Assert.Equal(["mine"], titles);
    }

    [Fact]
    public async Task A_note_I_cannot_open_adds_nothing_and_a_shared_note_adds_its_to_dos()
    {
        using var t = new TestDb();
        var owner = t.AddUser("n4"); var me = t.AddUser("n5");
        var page = await PageAsync(t, owner.Id, title: "Private plan");
        Todo(t, page, me.Id, Day(1), "assigned to me but I have no access");
        Assert.Empty((await Feed(t, me.Id)).Where(i => i.Source == "notes"));

        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(me.Id, null, NoteLevels.View), Ct);
        Assert.Single((await Feed(t, me.Id)).Where(i => i.Source == "notes"));

        // access removed again: gone from the calendar
        var share = t.Db.NoteShares.Single(s => s.PageId == page && s.UserId == me.Id);
        t.Db.NoteShares.Remove(share); t.Db.SaveChanges();
        Assert.Empty((await Feed(t, me.Id)).Where(i => i.Source == "notes"));
    }

    [Fact]
    public async Task A_sub_page_of_a_shared_page_counts_and_a_trashed_page_does_not()
    {
        using var t = new TestDb();
        var owner = t.AddUser("n6"); var me = t.AddUser("n7");
        var parent = await PageAsync(t, owner.Id, title: "Handbook");
        var child = await PageAsync(t, owner.Id, parent, "Chapter");
        await Svc(t).AddShareAsync(owner.Id, parent, new AddNoteShareRequest(me.Id, null, NoteLevels.View), Ct);
        Todo(t, child, me.Id, Day(3), "in the chapter");
        Assert.Single((await Feed(t, me.Id)).Where(i => i.Source == "notes"));

        Assert.True((await Svc(t).TrashAsync(owner.Id, parent, Ct)).Success);
        Assert.Empty((await Feed(t, me.Id)).Where(i => i.Source == "notes"));
    }

    [Fact]
    public async Task The_owner_of_a_trashed_page_does_not_see_its_to_dos_either()
    {
        using var t = new TestDb();
        var me = t.AddUser("n8");
        var page = await PageAsync(t, me.Id);
        Todo(t, page, me.Id, Day(1), "x");
        Assert.True((await Svc(t).TrashAsync(me.Id, page, Ct)).Success);
        Assert.Empty((await Feed(t, me.Id)).Where(i => i.Source == "notes"));
    }
}
