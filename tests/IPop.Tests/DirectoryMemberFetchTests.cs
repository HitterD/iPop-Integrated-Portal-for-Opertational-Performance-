using IPop.Web.Services;
using Xunit;
namespace IPop.Tests;
public class DirectoryMemberFetchTests
{
    private static DirectoryEmployeeDto Person(string id) => new(id, "Jane", null, null, null, null, null, null, "", null, null, "off");

    [Fact]
    public async Task Full_scope_accumulates_every_page_only_while_snapshot_is_current()
    {
        var pages = new List<int>();
        var result = await DirectoryMemberFetch.LoadAsync(page => { pages.Add(page); return Task.FromResult<DirectoryPageDto?>(new(2, page, 1, [Person(page.ToString())])); }, () => true);
        Assert.Equal(new[] { 1, 2 }, pages);
        Assert.Equal(new[] { "1", "2" }, result);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("selection")]
    [InlineData("generation")]
    [InlineData("disposed")]
    public async Task Changed_snapshot_after_await_discards_stale_members_without_fetching_next_page(string changed)
    {
        var pending = new TaskCompletionSource<DirectoryPageDto?>();
        var account = "A"; var selection = "unit-A"; var generation = 1; var disposed = false; var calls = 0;
        bool Current() => account == "A" && selection == "unit-A" && generation == 1 && !disposed;
        var task = DirectoryMemberFetch.LoadAsync(_ => { calls++; return pending.Task; }, Current);
        switch (changed) { case "account": account = "B"; break; case "selection": selection = "unit-B"; break; case "generation": generation++; break; default: disposed = true; break; }
        pending.SetResult(new(2, 1, 1, [Person("stale")]));
        Assert.Null(await task);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalid_snapshot_before_fetch_never_calls_backend()
    {
        var calls = 0;
        Assert.Null(await DirectoryMemberFetch.LoadAsync(_ => { calls++; return Task.FromResult<DirectoryPageDto?>(null); }, () => false));
        Assert.Equal(0, calls);
    }
}
