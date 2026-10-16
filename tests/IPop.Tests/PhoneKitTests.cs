using Bunit;
using IPop.Web.Components.Phone;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Phone kit (Wave 1): presentational building blocks shared by every phone screen.
public class PhoneKitTests
{
    private static BunitContext Ctx()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        return ctx;
    }

    [Fact]
    public void PhoneScreen_renders_nav_large_title_and_slots()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<PhoneScreen>(p => p
            .Add(x => x.Title, "Chat")
            .Add(x => x.Trailing, (RenderFragment)(b => b.AddMarkupContent(0, "<button class=\"t\">+</button>")))
            .Add(x => x.Below, (RenderFragment)(b => b.AddMarkupContent(0, "<div class=\"below\">search</div>")))
            .AddChildContent("<p class=\"body\">rows</p>"));
        Assert.Equal("Chat", cut.Find(".ph-nav .ph-nav-title").TextContent);
        Assert.Equal("Chat", cut.Find(".ph-scroll .ph-large-title").TextContent.Trim());
        Assert.Single(cut.FindAll(".ph-nav .t"));
        Assert.Single(cut.FindAll(".ph-scroll .below"));
        Assert.Single(cut.FindAll(".ph-scroll .body"));
    }

    [Fact]
    public void PhoneScreen_back_button_is_labelled_and_fires()
    {
        using var ctx = Ctx();
        var backs = 0;
        var cut = ctx.Render<PhoneScreen>(p => p
            .Add(x => x.Title, "Jason")
            .Add(x => x.LargeTitle, false)
            .Add(x => x.BackLabel, "Chat")
            .Add(x => x.OnBack, () => backs++));
        Assert.Empty(cut.FindAll(".ph-large-title"));
        Assert.Contains("is-collapsed", cut.Find(".ph-nav").ClassName);
        var back = cut.Find(".ph-back");
        Assert.Equal("Back to Chat", back.GetAttribute("aria-label"));
        back.Click();
        Assert.Equal(1, backs);
    }

    [Fact]
    public void BottomSheet_renders_nothing_when_closed()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<BottomSheet>(p => p.Add(x => x.Open, false).AddChildContent("<p>x</p>"));
        Assert.Empty(cut.Markup.Trim());
        Assert.Empty(ctx.Services.GetRequiredService<OverlayStack>().Stack);
    }

    [Fact]
    public void BottomSheet_open_is_a_dialog_with_a_decorative_grab_and_an_overlay_hook()
    {
        using var ctx = Ctx();
        bool? changed = null;
        var cut = ctx.Render<BottomSheet>(p => p
            .Add(x => x.Open, true).Add(x => x.Title, "New task").Add(x => x.Size, SheetSize.Tall)
            .Add(x => x.OpenChanged, v => changed = v)
            .AddChildContent("<p class=\"c\">body</p>"));
        var sheet = cut.Find(".ph-sheet");
        Assert.Equal("dialog", sheet.GetAttribute("role"));
        Assert.Equal("true", sheet.GetAttribute("aria-modal"));
        Assert.Equal("New task", sheet.GetAttribute("aria-label"));
        Assert.Contains("ph-sheet-tall", sheet.ClassName);
        var grab = cut.Find(".ph-grab");
        Assert.Equal("true", grab.GetAttribute("aria-hidden"));
        Assert.False(grab.HasAttribute("aria-label"));
        Assert.Single(ctx.Services.GetRequiredService<OverlayStack>().Stack);   // Esc / Back close it
        cut.Find(".ph-scrim").Click();
        Assert.False(changed);
    }

    [Fact]
    public async Task BottomSheet_closes_on_escape_through_the_overlay_stack()
    {
        using var ctx = Ctx();
        bool? changed = null;
        var cut = ctx.Render<BottomSheet>(p => p.Add(x => x.Open, true).Add(x => x.OpenChanged, v => changed = v));
        Assert.True(await cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync()));
        Assert.False(changed);
    }

    [Fact]
    public void Fab_is_labelled_and_hidden_when_not_visible()
    {
        using var ctx = Ctx();
        var clicks = 0;
        var cut = ctx.Render<Fab>(p => p.Add(x => x.Icon, "pencil").Add(x => x.Label, "New chat").Add(x => x.OnClick, () => clicks++));
        var b = cut.Find("button.ph-fab");
        Assert.Equal("New chat", b.GetAttribute("aria-label"));
        Assert.Contains("New chat", b.TextContent);
        b.Click();
        Assert.Equal(1, clicks);
        cut.Render(p => p.Add(x => x.Visible, false));
        Assert.Empty(cut.FindAll(".ph-fab"));
    }

    [Fact]
    public void ListRow_is_a_link_with_href_else_a_button()
    {
        using var ctx = Ctx();
        var link = ctx.Render<ListRow>(p => p.Add(x => x.Title, "Jason").Add(x => x.Subtitle, "Oke").Add(x => x.Href, "/chat/1").Add(x => x.Chevron, true).Add(x => x.Unread, true));
        var a = link.Find("a.ph-row");
        Assert.Equal("/chat/1", a.GetAttribute("href"));
        Assert.Contains("is-unread", a.ClassName);
        Assert.Single(link.FindAll(".ph-row-chev"));
        Assert.Equal("Oke", link.Find(".ph-row-sub").TextContent);
        var clicked = 0;
        var btn = ctx.Render<ListRow>(p => p.Add(x => x.Title, "Edit tabs").Add(x => x.OnClick, () => clicked++));
        btn.Find("button.ph-row").Click();
        Assert.Equal(1, clicked);
        Assert.Empty(btn.FindAll(".ph-row-chev"));
    }

    [Fact]
    public void ListSection_renders_header_and_inset_group()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<ListSection>(p => p.Add(x => x.Header, "Pinned").AddChildContent("<span class=\"r\">row</span>"));
        Assert.Equal("Pinned", cut.Find(".ph-sec").TextContent);
        Assert.Single(cut.FindAll(".ph-group .r"));
    }

    [Fact]
    public void SegmentedControl_marks_the_selected_tab_with_a_string_and_fires()
    {
        using var ctx = Ctx();
        string? picked = null;
        var cut = ctx.Render<SegmentedControl>(p => p
            .Add(x => x.Items, new List<SegmentItem> { new("and", "Android"), new("ios", "iPhone", 2) })
            .Add(x => x.Value, "and")
            .Add(x => x.ValueChanged, v => picked = v));
        Assert.Equal("tablist", cut.Find(".ph-seg").GetAttribute("role"));
        var tabs = cut.FindAll("[role=tab]");
        Assert.Equal("true", tabs[0].GetAttribute("aria-selected"));
        Assert.Equal("false", tabs[1].GetAttribute("aria-selected"));
        Assert.Contains("2", tabs[1].TextContent);
        tabs[1].Click();
        Assert.Equal("ios", picked);
    }
}
