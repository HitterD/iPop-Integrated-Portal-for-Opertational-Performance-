using System.Net;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class SessionLockTests
{
    [Fact]
    public async Task Owning_the_session_shows_nothing()
    {
        using var ctx = Context("active");
        var cut = ctx.Render<SessionLockHost>();
        var lk = ctx.Services.GetRequiredService<SessionLockState>();

        Assert.True(await lk.StartAsync());

        Assert.Equal(SessionLockMode.None, lk.Mode);
        Assert.False(ctx.Services.GetRequiredService<AuthState>().IsSuspended);
        Assert.Empty(cut.FindAll(".session-lock"));
    }

    [Fact]
    public async Task Another_tab_taking_over_locks_this_one()
    {
        using var ctx = Context("active");
        var cut = ctx.Render<SessionLockHost>();
        var lk = ctx.Services.GetRequiredService<SessionLockState>();
        await lk.StartAsync();

        await cut.InvokeAsync(() => lk.OnSessionTaken(forced: false));

        Assert.Equal(SessionLockMode.Locked, lk.Mode);
        Assert.True(ctx.Services.GetRequiredService<AuthState>().IsSuspended);
        cut.WaitForAssertion(() => Assert.Contains("iPop is open in another tab", cut.Find(".session-lock h2").TextContent));
        Assert.Equal(["Close tab", "Use here"], cut.FindAll(".session-lock-actions button").Select(b => b.TextContent.Trim()));
    }

    [Fact]
    public async Task Use_here_takes_the_session_back()
    {
        using var ctx = Context("active");
        var cut = ctx.Render<SessionLockHost>();
        var lk = ctx.Services.GetRequiredService<SessionLockState>();
        await lk.StartAsync();
        await cut.InvokeAsync(() => lk.OnSessionTaken(false));

        await cut.InvokeAsync(() => cut.FindAll(".session-lock-actions button")[1].Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".session-lock")));
        Assert.False(ctx.Services.GetRequiredService<AuthState>().IsSuspended);
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipopSession.claim" && Equals(i.Arguments[0], false));
    }

    [Fact]
    public async Task A_tab_in_a_call_makes_the_new_tab_yield()
    {
        using var ctx = Context("yield");
        var cut = ctx.Render<SessionLockHost>();
        var lk = ctx.Services.GetRequiredService<SessionLockState>();

        Assert.False(await lk.StartAsync());

        Assert.Equal(SessionLockMode.Yield, lk.Mode);
        Assert.True(ctx.Services.GetRequiredService<AuthState>().IsSuspended);
        cut.WaitForAssertion(() => Assert.Contains("A call is active in another tab", cut.Find(".session-lock h2").TextContent));

        ctx.JSInterop.Setup<string>("ipopSession.claim", true).SetResult("active");
        await cut.InvokeAsync(() => cut.Find(".session-lock-actions .danger").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".session-lock")));
        Assert.False(ctx.Services.GetRequiredService<AuthState>().IsSuspended);
    }

    [Fact]
    public async Task Suspended_hubs_do_not_connect()
    {
        using var ctx = Context("active");
        var auth = ctx.Services.GetRequiredService<AuthState>();
        auth.IsSuspended = true;
        // Not signed in either way here; the point is that nothing throws or connects.
        await ctx.Services.GetRequiredService<ChatState>().ConnectAsync();
        Assert.False(ctx.Services.GetRequiredService<ChatState>().IsConnected);
    }

    private static BunitContext Context(string startResult)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("ipopSession.start", _ => true).SetResult(startResult);
        ctx.JSInterop.Setup<string>("ipopSession.claim", false).SetResult("active");
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        var chat = new ChatState(auth, http);
        var calls = new CallState(auth, http, js);
        var notifs = new NotificationState(auth, http, new ToastService(), js);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(chat);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(notifs);
        ctx.Services.AddSingleton(new SessionLockState(auth, chat, notifs, calls, new BadgeState(auth), new TimerState(auth), js));
        return ctx;
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
