using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Notifier;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// /notifier/connect (1.4.3): "iPop Notifier wants to connect" — Authorize sends the tab back to the app's loopback.
public class NotifierConnectPageTests
{
    private static readonly string State = NotifierLoopback.NewSecret();
    private static readonly string Challenge = NotifierLoopback.NewSecret();
    private const string Redirect = "http://127.0.0.1:53682/callback";

    private sealed class Api(HttpStatusCode authorize = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? AuthorizeBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method.Method + " " + path);
            HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
            if (path == "/api/notifier/link/authorize")
            {
                AuthorizeBody = await request.Content!.ReadAsStringAsync(ct);
                return authorize == HttpStatusCode.OK ? Json(new { code = "CODE-" + new string('x', 38), shortCode = "ABCD-2345", expiresIn = 120 }) : new(authorize);
            }
            return path switch
            {
                "/api/directory/me" => Json(new { nik = "0001", name = "BAGASTYO PUTRA", roles = Array.Empty<string>() }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
    }

    private static string Url(string? state = null, string? challenge = null, string? redirect = null, string pc = "SJA-PC") =>
        "/notifier/connect?state=" + Uri.EscapeDataString(state ?? State) + "&challenge=" + Uri.EscapeDataString(challenge ?? Challenge)
        + "&redirect=" + Uri.EscapeDataString(redirect ?? Redirect) + "&pc=" + Uri.EscapeDataString(pc);

    private static (BunitContext Ctx, Api Api, NavigationManager Nav) Ctx(string url, HttpStatusCode authorize = HttpStatusCode.OK)
    {
        var api = new Api(authorize);
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(url);
        return (ctx, api, nav);
    }

    [Fact]
    public void Shows_the_pc_and_the_account_and_does_nothing_until_authorize()
    {
        var (ctx, api, _) = Ctx(Url());
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("iPop Notifier wants to connect", cut.Markup));
        Assert.Contains("SJA-PC", cut.Markup);
        Assert.Contains("as Bagastyo Putra", cut.Markup);
        Assert.Contains("Only continue if you just chose", cut.Markup);
        Assert.Equal("Authorize", cut.Find("button.nc-btn.primary").TextContent);
        Assert.Equal("Decline", cut.Find("button.nc-btn.secondary").TextContent);
        Assert.DoesNotContain(api.Calls, c => c.Contains("authorize"));
    }

    [Fact]
    public void Authorize_mints_the_code_and_sends_the_tab_to_the_loopback_with_code_and_state()
    {
        var (ctx, api, nav) = Ctx(Url());
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Authorize", cut.Markup));
        cut.Find("button.nc-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.StartsWith(Redirect + "?code=", nav.Uri));
        Assert.Contains("&state=" + State, nav.Uri);
        Assert.Contains(Challenge, api.AuthorizeBody);
        Assert.Contains(State, api.AuthorizeBody);
        Assert.Contains("Connecting iPop Notifier", cut.Markup);
        // "Not connected?" with the short code comes a few seconds later.
        Assert.DoesNotContain("ABCD-2345", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("ABCD-2345", cut.Markup), NotifierConnect.CodeDelay + TimeSpan.FromSeconds(3));
        Assert.Contains("Not connected?", cut.Markup);
        Assert.Contains("Copy code", cut.Markup);
    }

    [Fact]
    public void Decline_sends_access_denied_and_mints_nothing()
    {
        var (ctx, api, nav) = Ctx(Url());
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Decline", cut.Markup));
        cut.Find("button.nc-btn.secondary").Click();
        Assert.Equal(Redirect + "?error=access_denied&state=" + State, nav.Uri);
        Assert.DoesNotContain(api.Calls, c => c.Contains("authorize"));
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://10.10.6.51:53682/callback")]
    [InlineData("http://127.0.0.1.evil.com:53682/callback")]
    [InlineData("http://127.0.0.1/callback")]
    public void A_redirect_that_is_not_this_pcs_loopback_gets_no_authorize_button(string redirect)
    {
        var (ctx, api, _) = Ctx(Url(redirect: redirect));
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("This link can't be used", cut.Markup));
        Assert.Empty(cut.FindAll("button.nc-btn.primary"));
        Assert.DoesNotContain(api.Calls, c => c.Contains("authorize"));
    }

    [Fact]
    public void A_link_without_the_apps_state_or_challenge_is_refused()
    {
        foreach (var url in new[] { Url(state: "short"), Url(challenge: ""), "/notifier/connect", "/notifier/connect?request=" + new string('r', 43) })
        {
            var (ctx, _, _) = Ctx(url);
            using var _ = ctx;
            var cut = ctx.Render<NotifierConnect>();
            cut.WaitForAssertion(() => Assert.Contains("This link can't be used", cut.Markup));
        }
    }

    [Fact]
    public void The_pc_name_is_plain_text()
    {
        var (ctx, _, _) = Ctx(Url(pc: "<img src=x onerror=alert(1)>"));
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("iPop Notifier wants to connect", cut.Markup));
        Assert.DoesNotContain("<img src=x", cut.Markup);
        Assert.Contains("&lt;img src=x", cut.Markup);
    }

    [Fact]
    public void Login_comes_back_to_the_card_with_its_query()
    {
        var back = "/notifier/connect?state=" + State + "&challenge=" + Challenge + "&redirect=" + Uri.EscapeDataString(Redirect) + "&pc=SJA-PC";
        Assert.Equal(back, IPop.Web.App.SafeReturn(back));
    }
}
