using Bunit;
using IPop.Web.Components.Chat;
using Xunit;

namespace IPop.Tests;

/// Chat › "Jump to latest" (scroll perf, 2026-11-27): the button is always rendered and switched by
/// class, so showing it never inserts a sibling next to a long message list (that restyled ~16k nodes).
public class ChatJumpToLatestButtonTests
{
    [Fact]
    public void Hidden_by_class_not_removed_when_at_the_bottom()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<JumpToLatestButton>(p => p.Add(x => x.Away, false).Add(x => x.NewCount, 0));
        var btn = cut.Find("button.jump-latest");
        Assert.Contains("jl-off", btn.ClassList);
        Assert.Equal("true", btn.GetAttribute("aria-hidden"));
        Assert.Equal("-1", btn.GetAttribute("tabindex"));
    }

    [Fact]
    public void Set_shows_the_round_button_then_the_new_messages_pill_without_a_parent_render()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<JumpToLatestButton>(p => p.Add(x => x.Away, false).Add(x => x.NewCount, 0));

        cut.InvokeAsync(() => cut.Instance.Set(away: true, newCount: 0));
        var btn = cut.Find("button.jump-latest");
        Assert.DoesNotContain("jl-off", btn.ClassList);
        Assert.Null(btn.GetAttribute("aria-hidden"));
        Assert.Equal("Jump to latest", btn.GetAttribute("aria-label"));

        cut.InvokeAsync(() => cut.Instance.Set(away: true, newCount: 3));
        btn = cut.Find("button.jump-latest");
        Assert.Contains("has-new", btn.ClassList);
        Assert.Contains("3 new messages", btn.TextContent);

        cut.InvokeAsync(() => cut.Instance.Set(away: false, newCount: 0));
        Assert.Contains("jl-off", cut.Find("button.jump-latest").ClassList);
    }

    [Fact]
    public void Click_invokes_jump()
    {
        using var ctx = new BunitContext();
        var jumped = 0;
        var cut = ctx.Render<JumpToLatestButton>(p => p.Add(x => x.Away, true).Add(x => x.OnJump, () => jumped++));
        cut.Find("button.jump-latest").Click();
        Assert.Equal(1, jumped);
    }
}
