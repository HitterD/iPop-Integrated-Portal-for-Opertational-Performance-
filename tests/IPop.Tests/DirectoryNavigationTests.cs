using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class DirectoryNavigationTests
{
    private static DirectoryStructureNodeDto Structure() => new([
        new("A", "Plant A", 3, [new("D", "Finance", 3, [new("U", "Accounting", 3)], [new("E", "Empty", 0, [])])]),
        new("B", "Plant B", 2, [new("D", "Finance", 2, [new("U", "Accounting", 2)])]),
        new("Z", "Empty plant", 0, [])], 12);
    [Fact]
    public void Roots_start_collapsed_and_no_unassigned_node_is_added()
    {
        var nav = new DirectoryNavigation(); nav.Load(Structure());
        Assert.Equal(["Plant A", "Plant B"], nav.Visible().Select(n => n.Name));
        Assert.Empty(nav.Expanded);
        Assert.DoesNotContain(nav.Nodes, n => n.Name is "Not assigned" or "No department");
    }
    [Fact]
    public void Search_retains_ancestors_temporarily_and_clear_restores_expansion()
    {
        var nav = new DirectoryNavigation(); nav.Load(Structure());
        nav.Expanded.Add("site/A");
        var previous = nav.Visible().Select(n => n.Key).ToArray();
        nav.Search = "Accounting";
        Assert.Equal(6, nav.Visible().Count());
        Assert.All(nav.Visible().Where(n => n.Kind != "div"), n => Assert.True(nav.IsOpen(n)));
        Assert.Single(nav.Expanded);
        nav.Search = "";
        Assert.Equal(previous, nav.Visible().Select(n => n.Key));
    }
    [Fact]
    public void Identical_codes_in_different_sites_have_distinct_occurrence_paths()
    {
        var nav = new DirectoryNavigation(); nav.Load(Structure());
        var units = nav.Nodes.Where(n => n.Kind == "div").ToArray();
        Assert.Equal(2, units.Select(n => n.Key).Distinct().Count());
        nav.SelectedKey = units[1].Key;
        Assert.Equal("B", nav.Selected!.SiteCode);
        Assert.Equal(["Plant B", "Finance", "Accounting"], nav.Path(nav.Selected).Select(n => n.Name));
    }
    [Fact]
    public void Reload_prunes_invalid_saved_selection_and_expansions()
    {
        var nav = new DirectoryNavigation { SelectedKey = "missing", Expanded = ["missing", "site/A"] };
        nav.Load(Structure());
        Assert.Null(nav.SelectedKey); Assert.Equal(["site/A"], nav.Expanded);
    }
    [Fact]
    public void Show_empty_reveals_only_real_zero_member_nodes()
    {
        var nav = new DirectoryNavigation(); nav.Load(Structure());
        nav.Expanded.UnionWith(nav.Nodes.Select(n => n.Key));
        Assert.DoesNotContain(nav.Visible(), n => n.Count == 0);
        nav.ShowEmpty = true;
        Assert.Equal(2, nav.Visible().Count(n => n.Count == 0));
        nav.Search = "Empty";
        Assert.Contains(nav.Visible(), n => n.Name == "Empty");
        Assert.Contains(nav.Visible(), n => n.Name == "Plant A");
    }
    [Fact]
    public void Independent_account_navigation_state_does_not_share_expansions()
    {
        var first = new DirectoryNavigation(); first.Load(Structure()); first.Expanded.Add("site/A"); first.SelectedKey = "site/A";
        var second = new DirectoryNavigation(); second.Load(Structure());
        Assert.Empty(second.Expanded); Assert.Null(second.Selected); Assert.False(second.ShowEmpty);
    }
}
