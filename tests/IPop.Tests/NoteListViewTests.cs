using IPop.Application.Notes;
using IPop.Web.Services;
using Xunit;
using static IPop.Web.Services.NoteListView;

namespace IPop.Tests;

public class NoteListViewTests
{
    private static NoteListItemDto N(string title, string kind = "page", bool mine = true, bool fav = false, bool guide = false,
        int hoursAgo = 1, string? excerpt = null, string owner = "Me", int createdDaysAgo = 5) =>
        new(Guid.NewGuid(), null, title, null, "owner", mine, false, fav, guide, 0, 0, DateTime.UtcNow.AddHours(-hoursAgo), owner, 0, kind,
            excerpt, null, null, null, null, null, DateTime.UtcNow.AddDays(-createdDaysAgo));

    [Fact]
    public void Notes_list_has_pages_only_and_boards_list_has_boards_only_and_neither_has_the_guide()
    {
        var all = new[] { N("A"), N("Board", "board"), N("Guide", guide: true) };
        Assert.Equal(["A"], Build(all, "page", Filter.All, null, Sort.Recent).Select(p => p.Title));
        Assert.Equal(["Board"], Build(all, "board", Filter.All, null, Sort.Recent).Select(p => p.Title));
    }

    [Fact]
    public void Filters_split_mine_shared_and_favorites()
    {
        var all = new[] { N("mine"), N("theirs", mine: false), N("star", fav: true, mine: false) };
        Assert.Equal(["mine"], Build(all, "page", Filter.Mine, null, Sort.Name).Select(p => p.Title));
        Assert.Equal(["star", "theirs"], Build(all, "page", Filter.Shared, null, Sort.Name).Select(p => p.Title));
        Assert.Equal(["star"], Build(all, "page", Filter.Favorites, null, Sort.Name).Select(p => p.Title));
    }

    [Fact]
    public void Search_looks_in_title_first_lines_and_owner_ignoring_case()
    {
        var all = new[] { N("Weekly ICT", excerpt: "status k3s"), N("SOP", excerpt: "Cek surat jalan"), N("Other", owner: "Dimas Saputra") };
        Assert.Equal(["Weekly ICT"], Build(all, "page", Filter.All, "weekly", Sort.Recent).Select(p => p.Title));
        Assert.Equal(["SOP"], Build(all, "page", Filter.All, "SURAT JALAN", Sort.Recent).Select(p => p.Title));
        Assert.Equal(["Other"], Build(all, "page", Filter.All, "dimas", Sort.Recent).Select(p => p.Title));
        Assert.Empty(Build(all, "page", Filter.All, "zzz", Sort.Recent));
    }

    [Fact]
    public void Sorting_by_recent_name_and_created()
    {
        var all = new[] { N("B", hoursAgo: 1, createdDaysAgo: 30), N("A", hoursAgo: 50, createdDaysAgo: 1), N("C", hoursAgo: 10, createdDaysAgo: 10) };
        Assert.Equal(["B", "C", "A"], Build(all, "page", Filter.All, null, Sort.Recent).Select(p => p.Title));
        Assert.Equal(["A", "B", "C"], Build(all, "page", Filter.All, null, Sort.Name).Select(p => p.Title));
        Assert.Equal(["A", "C", "B"], Build(all, "page", Filter.All, null, Sort.Created).Select(p => p.Title));
    }

    [Fact]
    public void Group_headings_follow_the_calendar_day_not_24_hour_blocks()
    {
        var now = new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Today", Group(new DateTime(2026, 11, 19, 0, 30, 0, DateTimeKind.Utc), now));
        Assert.Equal("Yesterday", Group(new DateTime(2026, 11, 18, 23, 0, 0, DateTimeKind.Utc), now));
        Assert.Equal("This week", Group(now.AddDays(-4), now));
        Assert.Equal("This month", Group(now.AddDays(-20), now));
        Assert.Equal("Earlier", Group(now.AddDays(-90), now));
    }

    [Fact]
    public void Snippet_is_trimmed_with_an_ellipsis_and_never_null()
    {
        Assert.Equal("", Snippet(null));
        Assert.Equal("short", Snippet("  short "));
        var cut = Snippet(new string('x', 300), 50);
        Assert.Equal(50, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Theory]
    [InlineData("mine", Filter.Mine)] [InlineData("shared", Filter.Shared)] [InlineData("fav", Filter.Favorites)] [InlineData(null, Filter.All)] [InlineData("junk", Filter.All)]
    public void Filter_parsing_falls_back_to_all(string? s, Filter want) => Assert.Equal(want, ParseFilter(s));
}
