using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// More sheet → Edit tabs (mockup v1 frames g, h). Edit tabs opens on top of More, so phone Back
/// (OverlayStack, top-most first) closes Edit tabs and lands on the More sheet, then closes More.
public class MoreFlowTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IPop.Web"));

    [Fact]
    public void Edit_tabs_opens_on_top_of_More_and_closing_it_returns_to_More()
    {
        var f = new MoreFlow();
        f.ToggleMore();
        f.OpenEditTabs();
        Assert.True(f.MoreOpen);
        Assert.True(f.EditTabsOpen);
        f.SetEditTabs(false);          // Back / Cancel / Done
        Assert.True(f.MoreOpen);
        Assert.False(f.EditTabsOpen);
        f.SetMore(false);              // Back again
        Assert.False(f.MoreOpen);
    }

    [Fact]
    public void Navigating_or_tapping_More_again_closes_everything()
    {
        var f = new MoreFlow();
        f.ToggleMore(); f.OpenEditTabs();
        f.CloseAll();
        Assert.False(f.MoreOpen); Assert.False(f.EditTabsOpen);
        f.ToggleMore(); f.ToggleMore();
        Assert.False(f.MoreOpen);
    }

    [Fact]
    public void Closing_More_also_closes_Edit_tabs_on_top_of_it()
    {
        var f = new MoreFlow();
        f.ToggleMore(); f.OpenEditTabs();
        f.SetMore(false);
        Assert.False(f.EditTabsOpen);
    }

    [Fact]
    public void Back_popstate_on_the_same_page_keeps_More_open_but_a_real_navigation_closes_it()
    {
        var f = new MoreFlow();
        f.ToggleMore(); f.OpenEditTabs();
        f.SetEditTabs(false);                                       // OverlayStack closed Edit tabs on Back
        f.OnLocationChanged("http://h/chat");                       // ...then Blazor reports the same URL
        f.OnLocationChanged("http://h/chat");
        Assert.True(f.MoreOpen);
        f.OnLocationChanged("http://h/notes?x=1");
        Assert.False(f.MoreOpen);
    }

    [Fact]
    public void Main_layout_drives_the_sheets_through_MoreFlow()
    {
        var razor = File.ReadAllText(Path.Combine(Web, "Layout", "MainLayout.razor"));
        Assert.Contains("MoreFlow", razor);
        Assert.DoesNotContain("moreOpen = false; editTabsOpen = true", razor);
        Assert.Contains("more.OnLocationChanged(e.Location)", razor);
    }
}
