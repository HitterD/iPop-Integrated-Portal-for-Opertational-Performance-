using Bunit;
using IPop.Web.Components.Phone.Dashboard;
using Xunit;
namespace IPop.Tests;
public class PhoneDashboardTests
{
    [Fact]
    public void Dashboard_has_real_widgets_and_pencil_reorder_without_publish_permission_leak()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        IReadOnlyList<string>? saved = null;
        var cut = ctx.Render<DashboardPhone>(p => p.Add(x => x.Name, "Jane").Add(x => x.MailUnread, 3).Add(x => x.ChatUnread, 2)
            .Add(x => x.OnOrder, value => saved = value).Add(x => x.Agenda, new[] { new DashboardPhone.Item("Planning", "10:00", "/calendar") })
            .Add(x => x.News, new[] { new DashboardPhone.Item("Company news", "Today", "/news") }));
        Assert.Equal(4, cut.FindAll(".m-dashboard-counter").Count);
        Assert.Contains("Planning", cut.Markup); Assert.Contains("Company news", cut.Markup);
        Assert.Empty(cut.FindAll("a[href='/news?new=1']"));
        cut.Find("button[aria-label='Edit widgets']").Click();
        cut.Find("button[aria-label='Move counters up']").Click();
        Assert.Equal("counters", saved?[0]);
    }
    [Fact]
    public void Publisher_keeps_new_post_and_saved_order_is_respected()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<DashboardPhone>(p => p.Add(x => x.CanPostNews, true).Add(x => x.Order, new[] { "news", "agenda", "counters", "next" }));
        Assert.NotNull(cut.Find("a[href='/news?new=1']"));
        Assert.Equal("news", cut.Find("[data-widget]").GetAttribute("data-widget"));
    }
}
