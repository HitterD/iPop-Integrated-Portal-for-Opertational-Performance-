using Bunit;
using IPop.Web.Components.Phone;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// PhoneState mirrors PHONE_QUERY (js/mobile.js). Desktop-first: false until JS answers, so a
/// desktop never flashes phone markup.
public class PhoneStateTests
{
    [Fact]
    public void Defaults_to_desktop_before_js_answers()
    {
        using var ctx = new BunitContext();
        Assert.False(new PhoneState(ctx.JSInterop.JSRuntime).IsPhone);
    }

    [Fact]
    public async Task Start_reads_the_query_and_watches_it()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Setup<bool>("ipop.mobile.watch", _ => true).SetResult(true);
        var s = new PhoneState(ctx.JSInterop.JSRuntime);
        var fired = 0;
        s.Changed += () => fired++;
        await s.StartAsync();
        Assert.True(s.IsPhone);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Concurrent_start_waits_for_the_same_browser_detection()
    {
        using var ctx = new BunitContext();
        var pending = ctx.JSInterop.Setup<bool>("ipop.mobile.watch", _ => true);
        var state = new PhoneState(ctx.JSInterop.JSRuntime);
        var first = state.StartAsync(); var second = state.StartAsync();
        Assert.False(second.IsCompleted);
        pending.SetResult(true); await Task.WhenAll(first, second);
        Assert.True(state.IsPhone); Assert.Single(ctx.JSInterop.Invocations);
    }
    [Fact]
    public void Changed_fires_once_per_transition()
    {
        using var ctx = new BunitContext();
        var s = new PhoneState(ctx.JSInterop.JSRuntime);
        var fired = 0;
        s.Changed += () => fired++;
        s.OnPhoneChanged(false);           // no change
        s.OnPhoneChanged(true);
        s.OnPhoneChanged(true);            // repeat
        s.OnPhoneChanged(false);
        Assert.Equal(2, fired);
        Assert.False(s.IsPhone);
    }

    [Fact]
    public void PhoneOnly_and_DesktopOnly_follow_the_state()
    {
        using var ctx = new BunitContext();
        var s = new PhoneState(ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(s);
        var phone = ctx.Render<PhoneOnly>(p => p.AddChildContent("<b class=\"p\">phone</b>"));
        var desk = ctx.Render<DesktopOnly>(p => p.AddChildContent("<b class=\"d\">desk</b>"));
        Assert.Empty(phone.FindAll(".p"));
        Assert.Single(desk.FindAll(".d"));
        phone.InvokeAsync(() => s.OnPhoneChanged(true));
        phone.WaitForAssertion(() => Assert.Single(phone.FindAll(".p")));
        desk.WaitForAssertion(() => Assert.Empty(desk.FindAll(".d")));
    }
}
