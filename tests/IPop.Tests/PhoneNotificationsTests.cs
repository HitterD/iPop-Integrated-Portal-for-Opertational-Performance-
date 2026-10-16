using Bunit;
using IPop.Web.Components.Phone;
using IPop.Web.Components.Phone.Notifications;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Wave 2 phone notifications (mockup v2 NF1 / NF2). The page keeps loading, reading and snoozing.
public class PhoneNotificationsTests
{
    private static BunitContext Ctx()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        return ctx;
    }

    private static readonly DateTime Now = DateTime.Now;

    private static IReadOnlyList<PhoneNotifRow> Rows() =>
    [
        new(Guid.NewGuid(), "chat", "at-sign", "Alphin Santoso mentioned you", "ICT Team: @Bagastyo tolong cek AC", "09:38", "Today", false, null),
        new(Guid.NewGuid(), "meeting", "calendar", "Weekly ICT starts in 15 min", "Kalosi · 10:00–11:00", "09:45", "Today", false, "Until 10:45"),
        new(Guid.NewGuid(), "mail", "mail", "Hendra Wijaya", "Undangan Sosialisasi", "Yesterday", "Earlier", true, null),
    ];

    private static IReadOnlyList<SegmentItem> Chips() =>
        [new("all", "All"), new("unread", "Unread", 2), new("mention", "Mentions"), new("chat", "Chat"), new("mail", "Mail")];

    [Fact]
    public void List_groups_rows_with_module_icons_unread_dots_and_snooze_line()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<NotificationListPhone>(p => p.Add(x => x.Rows, Rows()).Add(x => x.Chips, Chips()).Add(x => x.Filter, "all"));

        Assert.Equal(["Today", "Earlier"], cut.FindAll(".m-nf-day").Select(d => d.TextContent.Trim()).ToList());
        var rows = cut.FindAll(".m-nf-row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("is-unread", rows[0].ClassName);
        Assert.DoesNotContain("is-unread", rows[2].ClassName);
        Assert.NotNull(rows[0].QuerySelector(".m-nf-ico.k-chat [data-lucide='at-sign']"));
        Assert.NotNull(rows[2].QuerySelector(".m-nf-ico.k-mail"));
        Assert.Contains("Until 10:45", rows[1].TextContent);
        Assert.Equal("09:38", rows[0].QuerySelector(".m-nf-tm")!.TextContent);
    }

    [Fact]
    public void Chips_are_tabs_with_string_aria_selected_and_change_the_filter()
    {
        using var ctx = Ctx();
        string? picked = null;
        var cut = ctx.Render<NotificationListPhone>(p => p.Add(x => x.Rows, Rows()).Add(x => x.Chips, Chips()).Add(x => x.Filter, "all")
            .Add(x => x.OnFilter, (string f) => picked = f));
        var chips = cut.FindAll(".m-nf-chips [role=tab]");
        Assert.Equal(5, chips.Count);
        Assert.Equal("true", chips[0].GetAttribute("aria-selected"));
        Assert.Equal("false", chips[1].GetAttribute("aria-selected"));
        Assert.Contains("2", chips[1].TextContent);
        chips[3].Click();
        Assert.Equal("chat", picked);
    }

    [Fact]
    public void Swipe_actions_mark_read_snooze_and_delete_and_tap_opens()
    {
        using var ctx = Ctx();
        var rows = Rows();
        var log = new List<string>();
        var cut = ctx.Render<NotificationListPhone>(p => p.Add(x => x.Rows, rows).Add(x => x.Chips, Chips()).Add(x => x.Filter, "all")
            .Add(x => x.OnOpen, (Guid id) => log.Add("open:" + rows.First(r => r.Id == id).Kind))
            .Add(x => x.OnToggleRead, (Guid id) => log.Add("read"))
            .Add(x => x.OnSnooze, (Guid id) => log.Add("snooze"))
            .Add(x => x.OnDelete, (Guid id) => log.Add("delete")));

        var first = cut.FindAll(".m-nf-sw")[0];
        Assert.Equal("", first.GetAttribute("data-swipe"));
        Assert.Equal(["Mark read", "Snooze"], first.QuerySelectorAll(".sw-acts.r button").Select(b => b.TextContent.Trim()).ToList());
        Assert.Equal(["Delete"], first.QuerySelectorAll(".sw-acts.l button").Select(b => b.TextContent.Trim()).ToList());
        Assert.Contains("Mark unread", cut.FindAll(".m-nf-sw")[2].QuerySelector(".sw-acts.r")!.TextContent);

        cut.FindAll(".m-nf-sw")[0].QuerySelector(".sw-acts.r button")!.Click();
        cut.FindAll(".m-nf-sw")[0].QuerySelectorAll(".sw-acts.r button")[1].Click();
        cut.FindAll(".m-nf-sw")[0].QuerySelector(".sw-acts.l button")!.Click();
        cut.FindAll(".m-nf-row")[2].Click();
        Assert.Equal(["read", "snooze", "delete", "open:mail"], log);
    }

    [Fact]
    public void Empty_list_shows_the_hint()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<NotificationListPhone>(p => p.Add(x => x.Rows, []).Add(x => x.Chips, Chips()).Add(x => x.Filter, "unread")
            .Add(x => x.EmptyHint, "You’ve read everything."));
        Assert.Contains("You’ve read everything.", cut.Find(".m-nf-empty").TextContent);
    }

    // ---------- NF2 ----------

    [Fact]
    public void Prefs_are_one_grouped_list_with_instant_switches_and_no_save_button()
    {
        using var ctx = Ctx();
        var toggled = new List<string>(); var dnd = 0; var more = 0;
        var cats = new List<PhoneNotifCategory> { new("mention", "Mentions", "at-sign", true), new("chat", "Chat", "message-circle", false) };
        var cut = ctx.Render<NotificationPrefsPhone>(p => p.Add(x => x.Dnd, false).Add(x => x.Categories, cats)
            .Add(x => x.OnDnd, () => dnd++).Add(x => x.OnCategory, (string c) => toggled.Add(c)).Add(x => x.OnMore, () => more++));

        Assert.Equal(["Quiet hours", "Categories"], cut.FindAll(".ph-sec").Select(s => s.TextContent.Trim()).Take(2).ToList());
        var sw = cut.FindAll("[role=switch]");
        Assert.Equal(3, sw.Count);
        Assert.Equal("false", sw[0].GetAttribute("aria-checked"));
        Assert.Equal("true", sw[1].GetAttribute("aria-checked"));
        Assert.Equal("false", sw[2].GetAttribute("aria-checked"));
        Assert.DoesNotContain("Save", cut.Markup);

        sw[0].Click(); sw[2].Click();
        cut.Find(".m-nf-more").Click();
        Assert.Equal((1, 1), (dnd, more));
        Assert.Equal(["chat"], toggled);
    }
}
