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
            .Add(x => x.OnOrder, value => saved = value)
            .Add(x => x.Agenda, new[] { new DashboardPhone.AgendaItem("10:00", "Planning", "Room 2", "/calendar", "meeting", false) })
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

    [Fact]
    public void V2_shows_hero_summary_next_meeting_join_late_badge_and_attention_actions()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        int? opened = null, completed = null;
        var cut = ctx.Render<DashboardPhone>(p => p.Add(x => x.Name, "Bagas").Add(x => x.SummaryText, "2 meetings today")
            .Add(x => x.NextTitle, "Weekly ICT sync").Add(x => x.NextTime, "10:00").Add(x => x.NextJoinable, true).Add(x => x.NextUrl, "/calls?room=x")
            .Add(x => x.OpenTasks, 8).Add(x => x.OverdueTasks, 2)
            .Add(x => x.Attention, new[] { new DashboardPhone.AttentionItem(0, "ack", "Memo", "Holiday schedule", "HR", "Acknowledge") })
            .Add(x => x.Tasks, new[] { new DashboardPhone.TaskItem(0, "Upload evidence", "Software sprint", "/projects/1", "Today", true) })
            .Add(x => x.Projects, new[] { new DashboardPhone.ProjectItem("Software sprint", "17 of 25 done", "/projects/1", 68) })
            .Add(x => x.OnAttention, i => opened = i).Add(x => x.OnCompleteTask, i => completed = i));
        Assert.Contains("Good morning, Bagas", cut.Find(".m-dash-hero").TextContent.Replace("Good afternoon", "Good morning").Replace("Good evening", "Good morning"));
        Assert.Contains("2 meetings today", cut.Find(".m-dash-hero").TextContent);
        Assert.Equal("Join", cut.Find(".m-dash-join").TextContent.Trim());
        Assert.Equal("/calls?room=x", cut.Find(".m-dash-join").GetAttribute("href"));
        Assert.Contains("2 late", cut.Markup);
        Assert.Equal("68%", cut.Find(".m-dash-ring").GetAttribute("data-p"));
        cut.Find(".m-dash-att").Click();
        Assert.Equal(0, opened);
        cut.Find("button[aria-label='Complete Upload evidence']").Click();
        Assert.Equal(0, completed);
    }

    [Fact]
    public void Clear_day_offers_schedule_instead_of_an_empty_meeting()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<DashboardPhone>(p => p.Add(x => x.Name, "Jane"));
        Assert.Contains("Your day is clear", cut.Markup);
        Assert.Equal("/calendar?new=1", cut.Find(".m-dash-join").GetAttribute("href"));
    }
}
