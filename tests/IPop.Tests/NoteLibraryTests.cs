using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Whiteboard library: my items, the team library, and what is refused.
public class NoteLibraryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());
    private const string Two = """[{"id":"a","type":"rect","x":0,"y":0,"w":10,"h":10},{"id":"b","type":"sticky","x":20,"y":0,"w":10,"h":10}]""";

    [Fact]
    public async Task Saved_items_are_mine_shared_ones_reach_everyone_and_only_the_owner_can_change_them()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var mine = (await Svc(t).SaveLibraryItemAsync(a.Id, new SaveLibraryItemRequest("Server rack", Two), Ct)).Data!;
        Assert.Equal(2, mine.Count);
        Assert.True(mine.Mine);
        Assert.Empty(await Svc(t).GetLibraryAsync(b.Id, Ct));                       // private by default

        var shared = await Svc(t).UpdateLibraryItemAsync(a.Id, mine.Id, new UpdateLibraryItemRequest(null, true), Ct);
        Assert.True(shared.Data!.Shared);
        var seen = Assert.Single(await Svc(t).GetLibraryAsync(b.Id, Ct));
        Assert.False(seen.Mine);
        Assert.Equal("Server rack", seen.Name);

        Assert.Equal("not_found", (await Svc(t).UpdateLibraryItemAsync(b.Id, mine.Id, new UpdateLibraryItemRequest("Mine now", false), Ct)).Error);
        Assert.Equal("not_found", (await Svc(t).DeleteLibraryItemAsync(b.Id, mine.Id, Ct)).Error);
        Assert.True((await Svc(t).DeleteLibraryItemAsync(a.Id, mine.Id, Ct)).Success);
        Assert.Empty(await Svc(t).GetLibraryAsync(a.Id, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"type":"rect"}]""")]
    [InlineData("""[{"id":5,"type":"rect"}]""")]
    [InlineData("""[1,2]""")]
    public async Task Anything_that_is_not_a_list_of_elements_is_refused(string json)
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        Assert.Equal("invalid_library_item", (await Svc(t).SaveLibraryItemAsync(a.Id, new SaveLibraryItemRequest("x", json), Ct)).Error);
    }

    [Fact]
    public async Task Too_big_is_refused_and_a_name_is_made_up_when_none_is_given()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var huge = "[" + string.Join(",", Enumerable.Range(0, 3000).Select(i => $$"""{"id":"e{{i}}","type":"rect"}""")) + "]";
        Assert.Equal("invalid_library_item", (await Svc(t).SaveLibraryItemAsync(a.Id, new SaveLibraryItemRequest("x", huge), Ct)).Error);
        var auto = (await Svc(t).SaveLibraryItemAsync(a.Id, new SaveLibraryItemRequest("   ", Two), Ct)).Data!;
        Assert.Equal("2 items", auto.Name);
        Assert.Equal(1, await t.Verify().NoteLibraryItems.CountAsync());
    }
}
